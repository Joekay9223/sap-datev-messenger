[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$ServiceName = 'NovaNein-Staging',
  [ValidatePattern('^[A-Za-z0-9_.\\-]+$')][string]$ServerInstance = 'localhost',
  [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,
  [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Die SQL-Leseberechtigung muss von einem lokalen Administrator eingerichtet werden.'
}
$service = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $ServiceName.Replace("'", "''")) -ErrorAction Stop
if (-not $service) { throw "Der Dienst $ServiceName wurde nicht gefunden." }
$login = switch ($service.StartName) {
  'LocalSystem' { 'NT AUTHORITY\SYSTEM'; break }
  'LocalService' { 'NT AUTHORITY\LOCAL SERVICE'; break }
  'NetworkService' { 'NT AUTHORITY\NETWORK SERVICE'; break }
  default { $service.StartName }
}
if ([string]::IsNullOrWhiteSpace($login) -or $login -notmatch '^(NT SERVICE|NT AUTHORITY)\\[A-Za-z0-9_. -]+$') {
  throw "Das Dienstkonto '$login' wird nicht automatisch als SQL-Login freigegeben. Ein dediziertes Konto muss ausdrücklich geprüft werden."
}

$quotedLogin = '[' + $login.Replace(']', ']]') + ']'
$quotedDatabase = '[' + $Database.Replace(']', ']]') + ']'
$sql = @"
IF SUSER_ID(N'$($login.Replace("'", "''"))') IS NULL
    CREATE LOGIN $quotedLogin FROM WINDOWS;
USE $quotedDatabase;
IF USER_ID(N'$($login.Replace("'", "''"))') IS NULL
    CREATE USER $quotedLogin FOR LOGIN $quotedLogin;
ALTER ROLE [db_datareader] ADD MEMBER $quotedLogin;
-- Frühere Skriptstände verweigerten CONTROL auf Datenbankebene; diese
-- übergeordnete Berechtigung schließt CONNECT ein und muss entfernt werden.
REVOKE CONTROL TO $quotedLogin;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER TO $quotedLogin;
"@

if (-not $Apply) {
  Write-Output "Prüfung erfolgreich: $login kann ausschließlich für lesenden Zugriff auf $Database eingerichtet werden. Mit -Apply bewusst ausführen."
  return
}

if ($PSCmdlet.ShouldProcess("$ServerInstance/$Database", "Dienstkonto $login als strikt lesenden SQL-Benutzer einrichten")) {
  Add-Type -AssemblyName System.Data
  $connection = [System.Data.SqlClient.SqlConnection]::new("Server=$ServerInstance;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connection Timeout=10")
  try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 30
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()
  }
  finally { $connection.Dispose() }
  Write-Output "SQL-Lesekonto für $ServiceName wurde eingerichtet; Datenänderungs-, Ausführungs- und Änderungsrechte sind ausdrücklich verweigert."
}
