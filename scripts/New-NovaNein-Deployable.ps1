[CmdletBinding()]
param(
    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\deployable'),
    [string] $Version = (Get-Date -Format 'yyyy.MM.dd-HHmm'),
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$packageName = "NovaNein-$Version-$RuntimeIdentifier"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = "$packageRoot.zip"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
foreach ($name in @('app', 'datev-bridge', 'config', 'scripts', 'docs')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot $name) | Out-Null
}

& $Dotnet publish (Join-Path $SourceRoot 'src\NovaNein.Server\NovaNein.Server.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o (Join-Path $packageRoot 'app')
if ($LASTEXITCODE -ne 0) { throw 'NovaNein.Server konnte nicht veröffentlicht werden.' }

& $Dotnet publish (Join-Path $SourceRoot 'src\NovaNein.DatevBridge\NovaNein.DatevBridge.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o (Join-Path $packageRoot 'datev-bridge')
if ($LASTEXITCODE -ne 0) { throw 'NovaNein.DatevBridge konnte nicht veröffentlicht werden.' }

$copyMap = @{
    'config\appsettings.Company.example.json' = 'config\appsettings.Company.example.json'
    'config\datev-bridge.production.example.json' = 'config\datev-bridge.production.example.json'
    'scripts\Install-NovaNein-Standalone.ps1' = 'scripts\Install-NovaNein-Standalone.ps1'
    'scripts\Install-NovaNein-DatevBridge.ps1' = 'scripts\Install-NovaNein-DatevBridge.ps1'
    'scripts\Set-NovaNein-OpenAiKey.ps1' = 'scripts\Set-NovaNein-OpenAiKey.ps1'
    'scripts\Set-NovaNein-ServiceSecrets.ps1' = 'scripts\Set-NovaNein-ServiceSecrets.ps1'
    'scripts\Set-NovaNein-DatevTransferCredential.ps1' = 'scripts\Set-NovaNein-DatevTransferCredential.ps1'
    'scripts\Protect-NovaNein-ServiceRegistry.ps1' = 'scripts\Protect-NovaNein-ServiceRegistry.ps1'
    'scripts\Grant-NovaNein-SapReadOnlySql.ps1' = 'scripts\Grant-NovaNein-SapReadOnlySql.ps1'
    'docs\deployment-quickstart.md' = 'docs\deployment-quickstart.md'
}

foreach ($entry in $copyMap.GetEnumerator()) {
    $source = Join-Path $SourceRoot $entry.Key
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Paketdatei fehlt: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $entry.Value) -Force
}

$forbidden = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.db', '.pfx', '.p12', '.pem', '.key') -or
    $_.Name -like '.env*' -or
    $_.Name -eq 'appsettings.Production.json'
}
if ($forbidden) {
    throw "Verbotene Laufzeit- oder Geheimnisdateien im Paket: $($forbidden.FullName -join ', ')"
}

$manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$manifestLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($manifestPath, $manifestLines, [Text.UTF8Encoding]::new($false))

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash

[pscustomobject]@{
    PackageRoot = $packageRoot
    ZipPath = $zipPath
    ZipSha256 = $zipHash
    FileCount = (Get-ChildItem -LiteralPath $packageRoot -Recurse -File).Count
} | ConvertTo-Json
