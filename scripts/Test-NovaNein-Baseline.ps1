[CmdletBinding()]
param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnetCandidates = @(
  $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
  (Join-Path $repositoryRoot '.dotnet\dotnet.exe'),
  (Join-Path $HOME '.dotnet-sdk\dotnet.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
$dotnet = $dotnetCandidates | Select-Object -First 1
if (-not $dotnet) {
  $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($dotnetCommand) { $dotnet = $dotnetCommand.Source }
}
if (-not $dotnet) { throw 'Es wurde keine .NET-SDK-Laufzeit gefunden.' }

Push-Location $repositoryRoot
try {
  function Invoke-Checked([string]$Label, [scriptblock]$Command) {
    Write-Host "`n== $Label ==" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Label ist mit Fehlercode $LASTEXITCODE fehlgeschlagen." }
  }

  if (-not $SkipRestore) { Invoke-Checked 'NuGet Restore' { & $dotnet restore NovaNein.sln } }
  Invoke-Checked 'Solution Build' { & $dotnet build NovaNein.sln --no-restore }
  Invoke-Checked '.NET Tests' { & $dotnet test NovaNein.sln --no-build --no-restore }
  Invoke-Checked 'Node Tests' { npm test }
  Invoke-Checked 'npm Audit' { npm audit --omit=dev }

  Write-Host "`n== NuGet Vulnerability Audit ==" -ForegroundColor Cyan
  $json = & $dotnet list NovaNein.sln package --vulnerable --include-transitive --format json
  if ($LASTEXITCODE -ne 0) { throw "NuGet-Sicherheitsprüfung ist mit Fehlercode $LASTEXITCODE fehlgeschlagen." }
  $audit = $json | ConvertFrom-Json
  $vulnerable = @($audit.projects | ForEach-Object {
    @($_.frameworks | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) })
  } | Where-Object { $_.vulnerabilities -and @($_.vulnerabilities).Count -gt 0 })
  if ($vulnerable.Count -gt 0) { throw "NuGet meldet $($vulnerable.Count) verwundbare Paketauflösungen." }

  Write-Host "`nNovaNein-Baseline ist grün." -ForegroundColor Green
}
finally { Pop-Location }
