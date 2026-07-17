[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$RegistrationPath = "$env:ProgramFiles\SAP\SAP Business One\AddOnsLocalRegistration.sbo",
  [string]$InstallPath = "$env:ProgramFiles\SAP\SAP Business One\AddOns\NovaNein\NovaNein",
  [string]$Version = '1.1.0.9',
  # Optional classic package metadata. Without these switches the script keeps
  # the existing installer attributes and remains a diagnose/repair tool only.
  [string]$InstallerName,
  [string]$InstallerMD5,
  [switch]$Apply,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }
if (-not (Test-Path -LiteralPath $RegistrationPath -PathType Leaf)) { throw "Die SAP-Registrierungsdatei fehlt: $RegistrationPath" }
if (-not (Test-Path -LiteralPath (Join-Path $InstallPath 'NovaNein.SapAddonHost.exe') -PathType Leaf)) { throw "Der NovaNein-Host fehlt: $InstallPath" }

[xml]$registration = Get-Content -LiteralPath $RegistrationPath -Raw
$addon = @($registration.ADDONS.AddOn | Where-Object { $_.Name -eq 'NovaNein' }) | Select-Object -First 1
if (-not $addon) { throw 'Der NovaNein-Eintrag wurde in AddOnsLocalRegistration.sbo nicht gefunden.' }

$desired = [ordered]@{
  Name = 'NovaNein'
  Path = $InstallPath
  Type = '1'
  Exe = ''
  ExeDir = ''
  X64Exe = 'NovaNein.SapAddonHost.exe'
  X64ExeDir = ''
  Ver = $Version
}
$current = [ordered]@{}
foreach ($key in $desired.Keys) { $current[$key] = [string]$addon.GetAttribute($key) }
$installerNameDesired = if ([string]::IsNullOrWhiteSpace($InstallerName)) { $current['Installer'] } else { $InstallerName.Trim() }
$installerMd5Desired = if ([string]::IsNullOrWhiteSpace($InstallerMD5)) { $current['InstallerMD5'] } else { $InstallerMD5.Trim().ToUpperInvariant() }
$installPackageMd5Desired = if ([string]::IsNullOrWhiteSpace($InstallerName)) { $current['InstallPkgMD5'] } else { '' }
$desired['Installer'] = $installerNameDesired
$desired['InstallerMD5'] = $installerMd5Desired
$desired['InstallPkgMD5'] = $installPackageMd5Desired
[pscustomobject]@{ RegistrationPath = (Resolve-Path $RegistrationPath).Path; Current = $current; Desired = $desired; Applied = $false }
if (-not $Apply) { return }

if ($Force -or $PSCmdlet.ShouldProcess($RegistrationPath, 'NovaNein-SAP-Registrierung mit x64-Host aktualisieren')) {
  $backup = "$RegistrationPath.backup-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
  Copy-Item -LiteralPath $RegistrationPath -Destination $backup -Force
  foreach ($key in $desired.Keys) { $addon.SetAttribute($key, [string]$desired[$key]) }
  $settings = [Xml.XmlWriterSettings]::new(); $settings.Indent = $false; $settings.Encoding = [Text.UnicodeEncoding]::new($false, $true)
  $writer = [Xml.XmlWriter]::Create($RegistrationPath, $settings)
  try { $registration.Save($writer) } finally { $writer.Dispose() }
  [pscustomobject]@{ RegistrationPath = $RegistrationPath; Backup = $backup; Current = $current; Desired = $desired; Applied = $true }
}
