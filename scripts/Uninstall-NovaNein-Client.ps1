[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$InstallDirectory = "$env:ProgramFiles\NovaNein\Client",
  [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }

function Get-CanonicalDirectoryPath([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Der Installationsordner darf nicht leer sein.' }
  $fullPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
  $root = [IO.Path]::GetPathRoot($fullPath)
  $separators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
  if ([string]::Equals($fullPath.TrimEnd($separators), $root.TrimEnd($separators), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ein Laufwerks- oder Freigabestamm darf niemals als NovaNein-Clientordner entfernt werden.'
  }
  return $fullPath.TrimEnd($separators)
}

function Test-IsSameOrChildPath([string]$Candidate, [string]$AllowedRoot) {
  return [string]::Equals($Candidate, $AllowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $Candidate.StartsWith($AllowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Unregister-NovaNeinCompanionTask {
  Unregister-ScheduledTask -TaskName 'NovaNein-Companion-AtLogOn' -Confirm:$false -ErrorAction SilentlyContinue
}

$allowedRoots = @(
  $env:ProgramFiles,
  $env:ProgramW6432,
  ${env:ProgramFiles(x86)}
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
  Get-CanonicalDirectoryPath (Join-Path $_ 'NovaNein\Client')
} | Sort-Object -Unique

$canonicalInstallDirectory = Get-CanonicalDirectoryPath $InstallDirectory
$sharedProgramDataRoot = Get-CanonicalDirectoryPath (Join-Path $env:ProgramData 'NovaNein')
if (Test-IsSameOrChildPath $canonicalInstallDirectory $sharedProgramDataRoot) {
  throw 'C:\ProgramData\NovaNein und seine Server-/Installer-Unterordner dürfen von der Client-Deinstallation niemals entfernt werden.'
}
$allowedRoot = $allowedRoots | Where-Object { Test-IsSameOrChildPath $canonicalInstallDirectory $_ } | Select-Object -First 1
if (-not $allowedRoot) {
  throw 'Der Client darf nur im freigegebenen Ordner Program Files\NovaNein\Client oder einem echten Unterordner davon deinstalliert werden.'
}
if ((Test-Path -LiteralPath $canonicalInstallDirectory) -and -not (Test-Path -LiteralPath $canonicalInstallDirectory -PathType Container)) {
  throw 'Der angegebene NovaNein-Installationspfad ist kein Ordner.'
}

# Path.GetFullPath beseitigt ..-Segmente, löst aber keine Junctions oder
# symbolischen Links auf. Deshalb wird jeder vorhandene Pfadabschnitt bis zur
# Allowlist-Grenze zusätzlich auf Reparse Points geprüft.
$currentPath = $canonicalInstallDirectory
while ($true) {
  if (Test-Path -LiteralPath $currentPath) {
    $item = Get-Item -LiteralPath $currentPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Der NovaNein-Clientordner darf keine Junction oder symbolische Verknüpfung enthalten: $currentPath"
    }
  }
  if ([string]::Equals($currentPath, $allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
  $parent = [IO.Path]::GetDirectoryName($currentPath)
  if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $currentPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Der NovaNein-Clientordner konnte nicht sicher bis zur Allowlist-Grenze geprüft werden.'
  }
  $currentPath = Get-CanonicalDirectoryPath $parent
}
$InstallDirectory = $canonicalInstallDirectory
if ($ValidateOnly) {
  [pscustomobject]@{ InstallDirectory = $InstallDirectory; AllowedRoot = $allowedRoot; Result = 'gültig' }
  return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent(); $principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDirectory `"$InstallDirectory`"" | Out-Null; return }
$settingsDirectory = Join-Path $env:ProgramData 'NovaNein'; $settings = Join-Path $settingsDirectory 'client.config'; $thumbprint = $null
if (Test-Path -LiteralPath $settings) { [xml]$config = Get-Content -Raw $settings; $thumbprint = $config.configuration.appSettings.add | Where-Object { $_.key -eq 'NovaNeinCertificateThumbprint' } | Select-Object -First 1 -ExpandProperty value }
if ($PSCmdlet.ShouldProcess($InstallDirectory, 'NovaNein-Client entfernen')) {
  Unregister-NovaNeinCompanionTask
  # Nur Hostprozesse beenden, deren tatsächliche EXE innerhalb dieses geprüften
  # Clientordners liegt. Gleichnamige Prozesse aus anderen Installationen bleiben unberührt.
  $hosts = Get-CimInstance Win32_Process -Filter "Name='NovaNein.SapAddonHost.exe'" -ErrorAction Stop | Where-Object {
    $_.ExecutablePath -and (Test-IsSameOrChildPath (Get-CanonicalDirectoryPath (Split-Path -Parent $_.ExecutablePath)) $InstallDirectory)
  }
  foreach ($hostProcess in $hosts) {
    Stop-Process -Id $hostProcess.ProcessId -Force -ErrorAction Stop
    Wait-Process -Id $hostProcess.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    if (Get-Process -Id $hostProcess.ProcessId -ErrorAction SilentlyContinue) { throw "Der laufende NovaNein-SAP-Host $($hostProcess.ProcessId) konnte nicht beendet werden." }
  }

  # Erst die Programmnutzlast vollständig und überprüfbar entfernen. Zertifikat und
  # Konfiguration bleiben erhalten, falls ein gesperrter Clientordner die Löschung verhindert.
  if (Test-Path -LiteralPath $InstallDirectory) {
    $quarantine = Join-Path (Split-Path -Parent $InstallDirectory) ('.client-uninstall-' + [Guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $InstallDirectory -Destination $quarantine -ErrorAction Stop
    try { Remove-Item -LiteralPath $quarantine -Recurse -Force -ErrorAction Stop }
    catch {
      if ((Test-Path -LiteralPath $quarantine) -and -not (Test-Path -LiteralPath $InstallDirectory)) {
        Move-Item -LiteralPath $quarantine -Destination $InstallDirectory -ErrorAction SilentlyContinue
      }
      throw
    }
    if (Test-Path -LiteralPath $InstallDirectory) { throw 'Der NovaNein-Clientordner ist nach der Deinstallation weiterhin vorhanden.' }
  }
  if ($thumbprint) { Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object { $_.Thumbprint -eq $thumbprint } | Remove-Item -Force -ErrorAction Stop }
  # C:\ProgramData\NovaNein wird auch vom zentralen Dienst und vom klassischen
  # SAP-Installer verwendet. Eine Client-Deinstallation darf deshalb niemals
  # den gemeinsamen Stammordner rekursiv entfernen.
  if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings -Force -ErrorAction Stop }
  if ((Test-Path -LiteralPath $settingsDirectory) -and -not (Get-ChildItem -LiteralPath $settingsDirectory -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $settingsDirectory -Force -ErrorAction SilentlyContinue
  }
  [pscustomobject]@{ RemovedCertificateThumbprint = $thumbprint; NextStep = 'Auf dem Server diesen Thumbprint mit --revoke-workstation sperren.' }
}
