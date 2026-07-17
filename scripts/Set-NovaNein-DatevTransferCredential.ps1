[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BridgeDirectory,
    [string] $CredentialTarget = 'NovaNein/DatevTransfer',
    [string] $UserName
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $BridgeDirectory 'NovaNein.DatevBridge.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Bridge-Programm fehlt: $exe" }
$arguments = @('credentials', 'set', '--target', $CredentialTarget)
if ($UserName) { $arguments += @('--username', $UserName) }
& $exe @arguments
if ($LASTEXITCODE -ne 0) { throw 'Der DATEV-Dateiserver-Zugang konnte nicht sicher gespeichert werden.' }
