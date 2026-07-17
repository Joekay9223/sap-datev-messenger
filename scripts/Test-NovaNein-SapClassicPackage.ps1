[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })][string]$PackageDirectory,
  [string]$PreviousRegisteredVersion
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$ardPath = Join-Path $package 'NovaNein.ard'
$installerPath = Join-Path $package 'NovaNein.SapAddonInstaller.exe'
if (-not (Test-Path -LiteralPath $ardPath -PathType Leaf)) { throw 'NovaNein.ard fehlt.' }
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'NovaNein.SapAddonInstaller.exe fehlt.' }

[xml]$registration = Get-Content -LiteralPath $ardPath
$addon = $registration.AddOnRegData.addon
if (-not $addon) { throw 'Die Datei ist keine klassische SAP-ARD mit einem <addon>-Element.' }
& (Join-Path $PSScriptRoot 'Test-NovaNein-SapClassicVersion.ps1') `
  -Version $addon.addonver `
  -PreviousRegisteredVersion $PreviousRegisteredVersion | Out-Null
if ($addon.platform -ne 'X' -or $addon.clienttype -ne 'W') { throw 'Die ARD ist nicht für den x64-Windows-Client registriert.' }
if ($addon.instname -ne 'NovaNein.SapAddonInstaller.exe' -or $addon.uninstname -ne 'NovaNein.SapAddonInstaller.exe') { throw 'Installer oder Deinstaller der ARD ist falsch.' }

$installerHash = (Get-FileHash -Algorithm MD5 -LiteralPath $installerPath).Hash
if ($addon.instsig -ne $installerHash -or $addon.uninstsig -ne $installerHash) { throw 'Die Installer-Signatur der ARD stimmt nicht.' }

$bytes = [IO.File]::ReadAllBytes($installerPath)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw ('Der Installer ist nicht AMD64 (PE machine 0x{0:X4}).' -f $machine) }

$assembly = [Reflection.Assembly]::Load($bytes)
$resourceNames = @($assembly.GetManifestResourceNames())
$required = @(
  'NovaNein.Payload.NovaNein.SapAddon.dll',
  'NovaNein.Payload.NovaNein.SapAddonHost.exe',
  'NovaNein.Payload.NovaNein.SapAddonHost.exe.config'
)
foreach ($name in $required) {
  if ($resourceNames -notcontains $name) { throw "Eingebettete Laufzeitdatei fehlt: $name" }
}

$hostStream = $assembly.GetManifestResourceStream('NovaNein.Payload.NovaNein.SapAddonHost.exe')
try {
  $md5 = [Security.Cryptography.MD5]::Create()
  try { $embeddedHostHash = ([BitConverter]::ToString($md5.ComputeHash($hostStream))).Replace('-', '') }
  finally { $md5.Dispose() }
}
finally { $hostStream.Dispose() }
if ($addon.addonsig -ne $embeddedHostHash) { throw 'AddonSig stimmt nicht mit dem eingebetteten Host überein.' }

[pscustomobject]@{
  PackageDirectory = $package
  Platform = $addon.platform
  ClientType = $addon.clienttype
  Version = $addon.addonver
  InstallerMd5 = $installerHash
  EmbeddedHostMd5 = $embeddedHostHash
  InstallerMachine = 'AMD64'
  Result = 'gültig'
}
