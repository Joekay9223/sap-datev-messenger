[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$PackageRoot = $PSScriptRoot,
  [string]$ServerUrl = '',
  [string]$InstallDirectory = "$env:ProgramFiles\NovaNein\Client",
  [string]$PfxPath,
  [string]$RootCertificatePath,
  [SecureString]$PfxPassword,
  [switch]$InstallCompanionTask,
  [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }

function Test-NovaNeinPayload([string]$PayloadDirectory, [string]$ManifestPath) {
  if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Das Integritätsmanifest fehlt: $ManifestPath" }
  $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
  if ($manifest.SchemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($manifest.ProductVersion)) { throw 'Das Client-Integritätsmanifest hat ein unbekanntes Format.' }
  if (@($manifest.Files).Count -eq 0) { throw 'Das Client-Integritätsmanifest enthält keine Dateien.' }
  $root = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  $expected = @{}
  foreach ($entry in @($manifest.Files)) {
    $relative = [string]$entry.Path
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[/\\])\.\.?([/\\]|$)' -or $relative -match '[<>:"|?*]') {
      throw "Ungültiger Pfad im Client-Integritätsmanifest: $relative"
    }
    $normalized = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath((Join-Path $PayloadDirectory $normalized))
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifestpfad verlässt die Client-Nutzlast: $relative" }
    if ($expected.ContainsKey($relative)) { throw "Doppelter Manifestpfad: $relative" }
    if ([int64]$entry.Length -lt 0 -or [string]$entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "Ungültige Integritätsdaten für: $relative" }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Nutzlastdatei fehlt: $relative" }
    $file = Get-Item -LiteralPath $full
    if ($file.Length -ne [int64]$entry.Length) { throw "Längenprüfung fehlgeschlagen: $relative" }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash
    if (-not [string]::Equals($actualHash, [string]$entry.Sha256, [StringComparison]::OrdinalIgnoreCase)) { throw "Hashprüfung fehlgeschlagen: $relative" }
    $expected[$relative] = $true
  }
  $actual = @(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File | ForEach-Object {
    $_.FullName.Substring($root.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
  })
  if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { -not $expected.ContainsKey($_) }).Count -gt 0) { throw 'Die Client-Nutzlast enthält Dateien, die nicht im Integritätsmanifest stehen.' }
  return $manifest
}

function Test-NovaNeinServiceConnection([string]$ServerUrl, [string]$CertificateThumbprint, [string]$ClientVersion) {
  Add-Type -AssemblyName System.Net.Http
  $certificate = Get-ChildItem "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction Stop
  $handler = [System.Net.Http.HttpClientHandler]::new()
  try {
    [void]$handler.ClientCertificates.Add($certificate)
    $client = [System.Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds(20)
    try {
      $uri = ([Uri]$ServerUrl).GetLeftPart([System.UriPartial]::Authority) + '/api/v1/client-health'
      $body = @{ clientVersion = $ClientVersion; clientKind = 'installer'; status = 'ok'; detail = 'Clientinstallation' } | ConvertTo-Json -Compress
      $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
      try { $response = $client.PostAsync($uri, $content).GetAwaiter().GetResult() }
      finally { $content.Dispose() }
      $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      if ($response.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw "Zentrale NovaNein-Selbstprüfung fehlgeschlagen: HTTP $([int]$response.StatusCode)." }
      $health = $payload | ConvertFrom-Json
      if (-not $health.compatible) { throw "Der Client ist nicht mit dem NovaNein-Server $($health.serverVersion) kompatibel." }
    }
    finally { $client.Dispose() }
  }
  finally { $handler.Dispose() }
}

function Register-NovaNeinCompanionTask([string]$HostPath) {
  $sapPath = Join-Path ${env:ProgramFiles} 'SAP\SAP Business One\SAP Business One.exe'
  if (-not (Test-Path -LiteralPath $sapPath -PathType Leaf)) { return $false }
  $interactiveUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
  if ([string]::IsNullOrWhiteSpace($interactiveUser)) { throw 'Der aktuell angemeldete Windows-Benutzer konnte für den SAP-Begleiter nicht ermittelt werden.' }
  $taskName = 'NovaNein-Companion-AtLogOn'
  $principal = New-ScheduledTaskPrincipal -UserId $interactiveUser -LogonType Interactive -RunLevel Limited
  $action = New-ScheduledTaskAction -Execute $HostPath -Argument '--companion' -WorkingDirectory (Split-Path -Parent $HostPath)
  $trigger = New-ScheduledTaskTrigger -AtLogOn -User $interactiveUser
  Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Description 'NovaNein Companion für SAP Business One' -Force | Out-Null
  return $true
}

function Unregister-NovaNeinCompanionTask {
  Unregister-ScheduledTask -TaskName 'NovaNein-Companion-AtLogOn' -Confirm:$false -ErrorAction SilentlyContinue
}

$packagedServerUrl = Join-Path $PackageRoot 'server-url.txt'
if (-not $PSBoundParameters.ContainsKey('ServerUrl') -and (Test-Path -LiteralPath $packagedServerUrl)) { $ServerUrl = (Get-Content -Raw $packagedServerUrl).Trim() }
$serverUri = $null
if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$serverUri) -or $serverUri.Scheme -ne [Uri]::UriSchemeHttps) { throw 'Die NovaNein-Serveradresse muss eine absolute HTTPS-Adresse sein.' }
$payload = Join-Path $PackageRoot 'payload'
$manifest = Test-NovaNeinPayload -PayloadDirectory $payload -ManifestPath (Join-Path $PackageRoot 'payload-manifest.json')
if ($ValidateOnly) {
  [pscustomobject]@{ PackageRoot = (Resolve-Path $PackageRoot).Path; ProductVersion = $manifest.ProductVersion; Files = @($manifest.Files).Count; Result = 'gültig' }
  return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  $companionArgument = if ($InstallCompanionTask) { ' -InstallCompanionTask' } else { '' }
  $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -PackageRoot `"$PackageRoot`" -ServerUrl `"$ServerUrl`" -InstallDirectory `"$InstallDirectory`"$companionArgument"
  $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
  if ($elevated.ExitCode -ne 0) { throw "Die erhöhte NovaNein-Installation ist mit Fehlercode $($elevated.ExitCode) beendet worden." }
  return
}

if (-not (Test-Path -LiteralPath (Join-Path $payload 'NovaNein.SapAddonHost.exe'))) { throw "Die Client-Nutzlast fehlt: $payload" }
if ([string]::IsNullOrWhiteSpace($PfxPath)) { $PfxPath = (Get-ChildItem (Join-Path $PackageRoot 'provisioning') -Filter 'novanein-*.pfx' -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName) }
if ([string]::IsNullOrWhiteSpace($RootCertificatePath)) { $RootCertificatePath = Join-Path $PackageRoot 'provisioning\novanein-staging-root-ca.cer' }
if (-not (Test-Path -LiteralPath $PfxPath) -or -not (Test-Path -LiteralPath $RootCertificatePath)) { throw 'Client-PFX oder Root-Zertifikat fehlen im Provisionierungspaket.' }
if (-not $PfxPassword) { $PfxPassword = Read-Host 'Einmaliges PFX-Passwort' -AsSecureString }

if ($PSCmdlet.ShouldProcess($InstallDirectory, 'NovaNein-Client installieren')) {
  $installParent = Split-Path -Parent $InstallDirectory
  New-Item -ItemType Directory -Force $installParent | Out-Null
  $stagedDirectory = "$InstallDirectory.install-$([Guid]::NewGuid().ToString('N'))"
  $backupDirectory = "$InstallDirectory.backup-$([Guid]::NewGuid().ToString('N'))"
  $payloadActivated = $false
  $companionTaskRegistered = $false
  $installed = $null
  $configPath = Join-Path (Join-Path $env:ProgramData 'NovaNein') 'client.config'
  $configBackup = "$configPath.backup-$([Guid]::NewGuid().ToString('N'))"
  try {
    New-Item -ItemType Directory -Force $stagedDirectory | Out-Null
    Copy-Item -Path (Join-Path $payload '*') -Destination $stagedDirectory -Recurse -Force
    $null = Test-NovaNeinPayload -PayloadDirectory $stagedDirectory -ManifestPath (Join-Path $PackageRoot 'payload-manifest.json')
    if (Test-Path -LiteralPath $InstallDirectory) { Move-Item -LiteralPath $InstallDirectory -Destination $backupDirectory -Force }
    Move-Item -LiteralPath $stagedDirectory -Destination $InstallDirectory -Force
    $payloadActivated = $true
    $installed = & (Join-Path $PackageRoot 'install-novanein-workstation-certificate.ps1') -PfxPath $PfxPath -RootCertificatePath $RootCertificatePath -PfxPassword $PfxPassword
  $settingsDirectory = Join-Path $env:ProgramData 'NovaNein'; New-Item -ItemType Directory -Force $settingsDirectory | Out-Null
  if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination $configBackup -Force }
  if (Test-Path -LiteralPath $configPath) { [xml]$config = Get-Content -Raw $configPath } else { [xml]$config = '<configuration><appSettings /></configuration>' }
  $appSettings = $config.SelectSingleNode('/configuration/appSettings')
  if (-not $appSettings) { $appSettings = $config.CreateElement('appSettings'); [void]$config.DocumentElement.AppendChild($appSettings) }
  $thumbprint = $installed.CertificateThumbprint
  if ([string]::IsNullOrWhiteSpace($thumbprint)) { throw 'Der Clientzertifikat-Thumbprint konnte nach dem Import nicht ermittelt werden.' }
  foreach ($pair in @{ NovaNeinServerUrl = $ServerUrl; NovaNeinCertificateThumbprint = $thumbprint }.GetEnumerator()) {
    $node = $appSettings.SelectSingleNode("add[@key='$($pair.Key)']")
    if (-not $node) { $node = $config.CreateElement('add'); $node.SetAttribute('key', $pair.Key); [void]$appSettings.AppendChild($node) }
    $node.SetAttribute('value', $pair.Value)
  }
  $config.Save($configPath)
  & icacls.exe $settingsDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Die Berechtigungen der NovaNein-Clientkonfiguration konnten nicht eingeschränkt werden.' }
  Test-NovaNeinServiceConnection -ServerUrl $ServerUrl -CertificateThumbprint $thumbprint -ClientVersion $manifest.ProductVersion
  $companionTaskRegistered = $InstallCompanionTask -and (Register-NovaNeinCompanionTask (Join-Path $InstallDirectory 'NovaNein.SapAddonHost.exe'))
  Remove-Item -LiteralPath $PfxPath -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $backupDirectory) { Remove-Item -LiteralPath $backupDirectory -Recurse -Force }
  [pscustomobject]@{ InstallDirectory = $InstallDirectory; ServerUrl = $ServerUrl; NextStep = 'Die zentrale SAP-Add-on-Zuweisung für diesen Arbeitsplatz prüfen. Aus diesem Zertifikatspaket keine ARD importieren.' }
  }
  catch {
    if ($companionTaskRegistered) { Unregister-NovaNeinCompanionTask }
    if ($installed -and $installed.CertificateThumbprint) { Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($installed.CertificateThumbprint)" -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $configBackup) { Move-Item -LiteralPath $configBackup -Destination $configPath -Force }
    if ($payloadActivated -and (Test-Path -LiteralPath $InstallDirectory)) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
    if (Test-Path -LiteralPath $backupDirectory) { Move-Item -LiteralPath $backupDirectory -Destination $InstallDirectory -Force }
    throw
  }
  finally {
    if (Test-Path -LiteralPath $stagedDirectory) { Remove-Item -LiteralPath $stagedDirectory -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $configBackup) { Remove-Item -LiteralPath $configBackup -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $backupDirectory) { Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction SilentlyContinue }
  }
}
