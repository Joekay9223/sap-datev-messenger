[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$PayloadDirectory,
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$ExtensionPackagePath,
  [Parameter(Mandatory)][string]$OutputPath,
  [ValidateNotNullOrEmpty()][string]$AddonName = 'NovaNein',
  [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$payload = (Resolve-Path $PayloadDirectory).Path
$packager = (Resolve-Path $ExtensionPackagePath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$stage = Join-Path ([IO.Path]::GetTempPath()) ('NovaNein-Lightweight-' + [Guid]::NewGuid().ToString('N'))
try {
  $x64 = Join-Path $stage 'X64Client'
  New-Item -ItemType Directory -Force $x64, (Split-Path -Parent $output) | Out-Null
  Get-ChildItem -LiteralPath $payload -File |
    Where-Object { $_.Extension -in '.exe','.dll','.config' } |
    Copy-Item -Destination $x64 -Force

  $sourceArd = Join-Path $stage "$AddonName.ard"
  & (Join-Path $repositoryRoot 'scripts\New-NovaNein-Ard.ps1') -PayloadDirectory $x64 -OutputPath $sourceArd -AddonName $AddonName -Version $Version | Out-Null
  # ExtensionPackage.exe derives the extension name from the ZIP base name.
  # Always package under the canonical add-on name and rename only after
  # validation, otherwise an output such as NovaNein-v20.zip silently creates
  # a different SAP extension instead of upgrading NovaNein.
  $packageOutput = Join-Path $stage "$AddonName.zip"
  Remove-Item -LiteralPath $output, $packageOutput -Force -ErrorAction SilentlyContinue

  $hostExe = Join-Path $x64 'NovaNein.SapAddonHost.exe'
  $arguments = @(
    '/64:"' + $hostExe + '"',
    '/s:"' + $sourceArd + '"',
    '/p:"' + $packageOutput + '"',
    '/ex:.pdb'
  )
  $process = Start-Process -FilePath $packager -ArgumentList $arguments -WorkingDirectory $stage -Wait -PassThru
  if ($process.ExitCode -ne 0) { throw "ExtensionPackage wurde mit Code $($process.ExitCode) beendet." }
  if (-not (Test-Path -LiteralPath $packageOutput)) { throw 'SAP ExtensionPackage hat kein ZIP-Paket erzeugt.' }

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [IO.Compression.ZipFile]::OpenRead($packageOutput)
  try {
    $names = @($zip.Entries | ForEach-Object FullName)
    if (-not ($names -contains 'X64Client/NovaNein.SapAddonHost.exe' -or $names -contains 'X64Client\NovaNein.SapAddonHost.exe')) {
      throw 'Das erzeugte ZIP enthält den x64-Add-on-Host nicht.'
    }
    if ($names | Where-Object { $_ -like '*.pdb' }) { throw 'Das erzeugte ZIP enthält unerwartete PDB-Dateien.' }
  }
  finally { $zip.Dispose() }
  $verify = Join-Path $stage 'verify'
  Expand-Archive -LiteralPath $packageOutput -DestinationPath $verify -Force
  $packagedArd = Join-Path $verify "$AddonName.ard"
  if (-not (Test-Path -LiteralPath $packagedArd)) { throw "Das ZIP enthält keine $AddonName.ard." }
  [xml]$packagedRegistration = Get-Content -Raw -LiteralPath $packagedArd
  if ($packagedRegistration.AddOnRegData.ExtName -ne $AddonName -or
      $packagedRegistration.AddOnRegData.Addons.Addon.Name -ne $AddonName) {
    throw "Das ZIP registriert nicht den erwarteten SAP-Add-on-Namen $AddonName."
  }
  Copy-Item -LiteralPath $packageOutput -Destination $output -Force
  Get-Item -LiteralPath $output
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
