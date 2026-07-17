[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $BridgeDirectory,
    [string] $ConfigurationPath = 'C:\ProgramData\NovaNein\Server\datev-bridge\datev-bridge.json',
    [string] $TaskName = 'NovaNein-DATEV-Bridge',
    [string] $UserId = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $BridgeDirectory 'NovaNein.DatevBridge.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Bridge-Programm fehlt: $exe" }
if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) { throw "Bridge-Konfiguration fehlt: $ConfigurationPath" }
if ($UserId -notmatch '\\') { throw 'UserId muss DOMAIN\Benutzer entsprechen.' }

$action = New-ScheduledTaskAction -Execute $exe -Argument "run-once --config `"$ConfigurationPath`"" -WorkingDirectory $BridgeDirectory
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$everyMinute = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1)

if ($PSCmdlet.ShouldProcess($TaskName, "Scheduled Task unter $UserId installieren")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($atLogon, $everyMinute) -Principal $principal -Settings $settings -Description 'Überträgt ausschließlich geprüfte geschlossene NovaNein-DATEV-ZIP-Pakete an BTTnext.' -Force | Out-Null
    $task = Get-ScheduledTask -TaskName $TaskName
    if ([int]$task.Principal.LogonType -ne 3) { throw 'Der Task wurde nicht mit InteractiveToken installiert.' }
    Write-Host "Task $TaskName wurde unter $UserId installiert."
}
