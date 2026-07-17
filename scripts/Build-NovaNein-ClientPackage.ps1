[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{1,62}$')][string]$WorkstationName,
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$PfxPath,
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$RootCertificatePath,
  [Parameter(Mandatory)][ValidatePattern('^https://')][string]$ServerUrl,
  [string]$ClientPayloadDirectory,
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("NovaNein-Client-" + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stage "NovaNein-$WorkstationName"
$payload = Join-Path $packageRoot 'payload'
try {
  New-Item -ItemType Directory -Force $payload, (Join-Path $packageRoot 'provisioning') | Out-Null
  if ($ClientPayloadDirectory) {
    if (-not (Test-Path -LiteralPath (Join-Path $ClientPayloadDirectory 'NovaNein.SapAddonHost.exe'))) { throw "Die vorveröffentlichte Client-Nutzlast ist unvollständig: $ClientPayloadDirectory" }
    Copy-Item -Path (Join-Path $ClientPayloadDirectory '*') -Destination $payload -Recurse -Force
  }
  else {
    & $dotnet publish (Join-Path $repositoryRoot 'src\NovaNein.SapAddonHost\NovaNein.SapAddonHost.csproj') -c Release -r win-x64 --self-contained false -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Das SAP-Add-on konnte nicht veröffentlicht werden.' }
  }
  Get-ChildItem -LiteralPath $payload -File |
    Where-Object { $_.Extension -notin '.exe','.dll','.config' } |
    Remove-Item -Force
  $payloadRoot = [IO.Path]::GetFullPath($payload).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  $manifestFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($payloadRoot.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
    [pscustomobject]@{
      Path = $relative
      Length = $_.Length
      Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
  })
  if ($manifestFiles.Count -eq 0) { throw 'Die Client-Nutzlast enthält keine Dateien.' }
  [pscustomobject]@{
    SchemaVersion = 1
    ProductVersion = '1.1.0'
    WorkstationName = $WorkstationName
    Files = $manifestFiles
  } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'payload-manifest.json') -Encoding utf8
  Copy-Item -LiteralPath $PfxPath -Destination (Join-Path $packageRoot "provisioning\novanein-$WorkstationName.pfx") -Force
  Copy-Item -LiteralPath $RootCertificatePath -Destination (Join-Path $packageRoot 'provisioning\novanein-staging-root-ca.cer') -Force
  foreach($script in 'Install-NovaNein-Client.ps1','Uninstall-NovaNein-Client.ps1','install-novanein-workstation-certificate.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $packageRoot -Force }
  Set-Content -LiteralPath (Join-Path $packageRoot 'server-url.txt') -Value $ServerUrl -NoNewline -Encoding utf8
  # Die SAP-Registrierung ist ein eigener, zentraler Deploymentvorgang. Eine
  # Schema-3-Source-ARD im individuellen Zertifikatspaket würde leicht erneut
  # fälschlich im klassischen SAP-Dialog geöffnet werden.
  $launcher = @"
@echo off
setlocal
title NovaNein Client-Installation
echo NovaNein Client wird installiert. Bitte bestaetigen Sie UAC und geben Sie das Einmal-PFX-Passwort ein.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-NovaNein-Client.ps1" -PackageRoot "%~dp0"
if errorlevel 1 (
  echo.
  echo Die NovaNein-Installation ist fehlgeschlagen. Der Fehler steht oben im Fenster.
  pause
  exit /b 1
)
echo.
echo NovaNein Client wurde erfolgreich installiert.
pause
"@
  $launcher = $launcher.TrimStart("`r", "`n") + "`r`n"
  Set-Content -LiteralPath (Join-Path $packageRoot 'Install-NovaNein-Client.cmd') -Value $launcher -NoNewline -Encoding ascii
  Set-Content -LiteralPath (Join-Path $packageRoot 'Start-NovaNein-Companion.cmd') -Value @"
@echo off
setlocal
title NovaNein SAP-Begleiter
set "TARGET=%ProgramFiles%\NovaNein\Client\NovaNein.SapAddonHost.exe"
if not exist "%TARGET%" (
  echo NovaNein ist noch nicht installiert. Bitte zuerst Install-NovaNein-Client.cmd ausfuehren.
  pause
  exit /b 1
)
start "NovaNein SAP-Begleiter" "%TARGET%" --companion
"@ -Encoding ascii
  Set-Content -LiteralPath (Join-Path $packageRoot 'README-Installation.txt') -Value @"
NovaNein Client – Installation

1. Dieses Paket ist ausschließlich für den Arbeitsplatz $WorkstationName bestimmt.
2. Install-NovaNein-Client.cmd als Administrator starten.
3. UAC bestätigen und das Einmal-PFX-Passwort eingeben.
4. Die Installation erst als erfolgreich betrachten, wenn der authentifizierte Health-Test HTTP 200 meldet.

Wenn SAP Business One wegen eines temporär blockierten Add-on-Managers nicht automatisch startet, kann danach Start-NovaNein-Companion.cmd verwendet werden. Der Begleiter wartet in derselben Windows-Sitzung auf die laufende SAP-Oberfläche und verbindet sich ohne einen fremden Add-on-Verbindungstoken.

Das individuelle Zertifikat darf nicht auf einen anderen PC kopiert werden.
Die Clients verbinden sich ausschließlich mit $ServerUrl; die Datenbank bleibt auf dem SAP-Server.
"@ -Encoding utf8
  New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
  $zip = Join-Path $OutputDirectory "NovaNein-Client-$WorkstationName.zip"
  if ($PSCmdlet.ShouldProcess($zip, 'Individuelles NovaNein-Clientpaket erzeugen')) { Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip -Force; Get-Item -LiteralPath $zip }
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
