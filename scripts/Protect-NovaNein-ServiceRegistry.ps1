[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$ServiceName = 'NovaNein-Staging'
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Die Dienst-Registry darf nur von einem lokalen Administrator geschützt werden.'
}
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
  throw "Der Dienst $ServiceName wurde nicht gefunden."
}

$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$registrySubKey = "SYSTEM\CurrentControlSet\Services\$ServiceName"
$service = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $ServiceName.Replace("'", "''")) -ErrorAction Stop

function Resolve-AccountSid([string]$AccountName) {
  $normalized = switch ($AccountName) {
    'LocalSystem' { 'NT AUTHORITY\SYSTEM'; break }
    'LocalService' { 'NT AUTHORITY\LOCAL SERVICE'; break }
    'NetworkService' { 'NT AUTHORITY\NETWORK SERVICE'; break }
    default { $AccountName }
  }
  try { return ([Security.Principal.NTAccount]::new($normalized)).Translate([Security.Principal.SecurityIdentifier]) }
  catch { return $null }
}

function Get-SidValue([Security.Principal.IdentityReference]$IdentityReference) {
  if ($IdentityReference -is [Security.Principal.SecurityIdentifier]) { return $IdentityReference.Value }
  try { return $IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
  catch { return $null }
}

if ($PSCmdlet.ShouldProcess($key, 'Registry-ACL auf SYSTEM, Administratoren und das Dienstkonto beschränken')) {
  $registryRights = [Security.AccessControl.RegistryRights]::ReadPermissions -bor [Security.AccessControl.RegistryRights]::ChangePermissions
  $registryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
    $registrySubKey,
    [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
    $registryRights)
  if (-not $registryKey) { throw "Der Dienst-Registry-Key wurde nicht gefunden: $key" }
  try {
    # Eine neue DACL vermeidet, dass verwaiste oder nicht mehr auflösbare
    # Konten aus einer alten Installation die Härtung blockieren.
    $acl = [Security.AccessControl.RegistrySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)

  $fullControlSids = @(
    [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
    [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
  )
  foreach ($sid in $fullControlSids) {
    $rule = [Security.AccessControl.RegistryAccessRule]::new(
      $sid,
      [Security.AccessControl.RegistryRights]::FullControl,
      [Security.AccessControl.InheritanceFlags]::ContainerInherit,
      [Security.AccessControl.PropagationFlags]::None,
      [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
  }

  $serviceSid = Resolve-AccountSid $service.StartName
  if ($serviceSid -and $fullControlSids.Value -notcontains $serviceSid.Value) {
    $serviceRule = [Security.AccessControl.RegistryAccessRule]::new(
      $serviceSid,
      [Security.AccessControl.RegistryRights]::ReadKey,
      [Security.AccessControl.InheritanceFlags]::ContainerInherit,
      [Security.AccessControl.PropagationFlags]::None,
      [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($serviceRule)
  }

    $registryKey.SetAccessControl($acl)
    $broadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    $unsafe = @($registryKey.GetAccessControl([Security.AccessControl.AccessControlSections]::Access).Access) | Where-Object {
      $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
      $broadSids -contains (Get-SidValue $_.IdentityReference)
    }
    if ($unsafe) { throw 'Die Dienst-Registry besitzt nach der Härtung weiterhin eine breite Leseberechtigung.' }
    Write-Output "Registry-ACL für $ServiceName wurde auf privilegierte Identitäten beschränkt."
  }
  finally { $registryKey.Dispose() }
}
