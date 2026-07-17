[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$PayloadDirectory,
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$AddOnRegDataGenPath,
  [Parameter(Mandatory)][string]$OutputDirectory,
  [string]$Version = '1.1.0.3',
  [Parameter(Mandatory)][string]$PreviousRegisteredVersion,
  [string]$ContactData = 'https://example.invalid/novanein',
  [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
& (Join-Path $PSScriptRoot 'Test-NovaNein-SapClassicVersion.ps1') `
  -Version $Version `
  -PreviousRegisteredVersion $PreviousRegisteredVersion | Out-Null
$payload = (Resolve-Path $PayloadDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$generator = (Resolve-Path $AddOnRegDataGenPath).Path
if (-not $DotNetPath) {
  $candidates = @(
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
    (Join-Path $env:USERPROFILE '.dotnet-sdk\dotnet.exe'),
    $(try { (Get-Command dotnet -ErrorAction Stop).Source } catch { $null })
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
  $DotNetPath = $candidates | Select-Object -First 1
}
if (-not $DotNetPath) { throw 'Es wurde kein .NET-8-SDK gefunden. DotNetPath muss auf dessen dotnet.exe zeigen.' }
if (-not (& $DotNetPath --list-sdks 2>$null)) { throw "DotNetPath enthält kein .NET-SDK: $DotNetPath" }

$build = Join-Path ([IO.Path]::GetTempPath()) ('NovaNein-Classic-' + [Guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Force $build, $output | Out-Null
  Remove-Item -LiteralPath (Join-Path $output 'NovaNein.ard'), (Join-Path $output 'NovaNein.xml'), (Join-Path $output 'NovaNein.SapAddonInstaller.exe') -Force -ErrorAction SilentlyContinue
  & $DotNetPath build (Join-Path $repositoryRoot 'src\NovaNein.SapAddonInstaller\NovaNein.SapAddonInstaller.csproj') -c Release -p:PayloadDirectory="$payload" -o $build
  if ($LASTEXITCODE -ne 0) { throw 'Der SAP-konforme NovaNein-Installer konnte nicht gebaut werden.' }

  $installer = Join-Path $output 'NovaNein.SapAddonInstaller.exe'
  Copy-Item -Force (Join-Path $build 'NovaNein.SapAddonInstaller.exe') $installer
  $definition = Join-Path $output 'NovaNein.xml'
  # SAP maps addongroup M to Manual; NovaNein must start automatically at company login.
  $xml = '<AddOnInfo partnername="NovaNein" partnernmsp="NovaNein" contdata="' + [Security.SecurityElement]::Escape($ContactData) + '" addonname="NovaNein" addongroup="A" platform="X" esttime="120" instparams="" silentinst="Y" unesttime="120" uncmdarg="/uninstall" silentuninst="Y" ugdesttime="120" ugdcmdargs="" silentugd="N" />'
  [IO.File]::WriteAllText($definition, $xml, [Text.UTF8Encoding]::new($false))

  $addonExe = Join-Path $payload 'NovaNein.SapAddonHost.exe'
  $arguments = @($definition, $Version, $installer, $installer, $addonExe) | ForEach-Object { '"' + ($_ -replace '"','\"') + '"' }
  $process = Start-Process -FilePath $generator -ArgumentList $arguments -WorkingDirectory $output -Wait -PassThru
  if ($process.ExitCode -ne 0) { throw "AddOnRegDataGen wurde mit Code $($process.ExitCode) beendet." }
  $ard = Join-Path $output 'NovaNein.ard'
  if (-not (Test-Path -LiteralPath $ard)) { throw 'Der offizielle SAP-Generator hat NovaNein.ard nicht erzeugt.' }
  [xml]$registration = Get-Content -LiteralPath $ard
  $addon = $registration.AddOnRegData.addon
  $installerHash = (Get-FileHash -Algorithm MD5 -LiteralPath $installer).Hash
  $addonHash = (Get-FileHash -Algorithm MD5 -LiteralPath $addonExe).Hash
  if ($addon.platform -ne 'X' -or $addon.clienttype -ne 'W' -or $addon.instname -ne 'NovaNein.SapAddonInstaller.exe') { throw 'Die erzeugte ARD enthält keine gültige Windows-x64-Installerdefinition.' }
  if ($addon.instsig -ne $installerHash -or $addon.addonsig -ne $addonHash) { throw 'Die MD5-Signaturen der erzeugten ARD stimmen nicht mit Installer und Add-on überein.' }
  Get-Item -LiteralPath $ard, $installer
}
finally { Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue }
