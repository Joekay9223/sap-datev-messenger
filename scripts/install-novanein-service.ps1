[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)] [string]$PublishPath,
  [string]$ServiceName = 'NovaNein',
  [string]$ServiceAccount = 'NT SERVICE\NovaNein',
  [string]$DataRoot = 'C:\ProgramData\NovaNein\Server',
  [string]$ServerCertificateThumbprint,
  [string]$ListenAddresses = '0.0.0.0',
  [int]$Port = 5189,
  [switch]$Repair,
  [switch]$Install
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PublishPath 'NovaNein.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish-Ausgabe fehlt: $exe" }
if (-not $Install) { Write-Host "Prüfung erfolgreich. Für die Installation mit -Install erneut ausführen."; exit 0 }
if ([string]::IsNullOrWhiteSpace($ServerCertificateThumbprint)) { throw 'Für die Dienstinstallation ist ein LocalMachine-Serverzertifikat-Thumbprint erforderlich.' }

foreach ($path in @($DataRoot, "$DataRoot\documents", "$DataRoot\packages", "$DataRoot\logs")) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
 $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService -and -not $Repair) { throw "Dienst $ServiceName existiert bereits; kein Überschreiben. Für eine kontrollierte Reparatur -Repair angeben." }
if ($existingService -and $existingService.Status -ne 'Stopped') { throw "Dienst $ServiceName muss für eine Reparatur beendet sein." }
if ($PSCmdlet.ShouldProcess($ServiceName, 'Windows-Dienst installieren')) {
  if (-not $existingService) { New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'NovaNein Belegarchiv' -Description 'Interner SAP-/DATEV-Belegarchivdienst' -StartupType Automatic }
  cmd.exe /c ("sc.exe config {0} obj= `"{1}`" password= `"`"" -f $ServiceName, $ServiceAccount) | Out-Null
  & (Join-Path $PSScriptRoot 'Protect-NovaNein-ServiceRegistry.ps1') -ServiceName $ServiceName
  & icacls.exe $DataRoot /inheritance:r /grant:r "$ServiceAccount`:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Datenordnerberechtigungen für das Dienstkonto konnten nicht gesetzt werden.' }
  $certificate = Get-ChildItem "Cert:\LocalMachine\My\$ServerCertificateThumbprint" -ErrorAction Stop
  $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
  try {
    if ($rsa -is [System.Security.Cryptography.RSACng]) { $keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName) }
    elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) { $keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName) }
    else { throw 'Der Serverzertifikatsschlüssel verwendet keinen unterstützten RSA-Anbieter.' }
    & icacls.exe $keyPath /grant "$ServiceAccount`:R" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Zugriff des Dienstkontos auf den Serverzertifikatsschlüssel konnte nicht gesetzt werden.' }
  }
  finally { if ($rsa) { $rsa.Dispose() } }
  $productionConfig = @{ Storage = @{ DatabasePath = "$DataRoot\novanein.db"; DocumentRoot = "$DataRoot\documents" }; Sap = @{ Mode = 'read-only' }; Datev = @{ AllowDirectTransfer = $false }; Tls = @{ ServerCertificateThumbprint = $ServerCertificateThumbprint; CertificateStoreLocation = 'LocalMachine'; ListenAddresses = $ListenAddresses; Port = $Port } }
  $productionConfig | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $PublishPath 'appsettings.Production.json')
  sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/""/0 | Out-Null
  Write-Host "Dienst angelegt. Datenordner: $DataRoot. Berechtigungen, TLS-Zertifikat, OpenAI- und SAP-Secrets sowie Watchfolder bleiben vor dem Start gesondert zu konfigurieren."
}
