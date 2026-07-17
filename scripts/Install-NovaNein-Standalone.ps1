[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $PackageRoot,
    [string] $ServiceName = 'NovaNein',
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'NovaNein'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'NovaNein\Server'),
    [string] $ListenAddress = '0.0.0.0',
    [ValidateRange(1, 65535)] [int] $Port = 5188,
    [string[]] $AllowedCidrs = @('192.0.2.0/24'),
    [switch] $Install,
    [switch] $Repair,
    [switch] $Start
)

$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$sourceApp = Join-Path $PackageRoot 'app'
$sourceExe = Join-Path $sourceApp 'NovaNein.Server.exe'

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "NovaNein.Server.exe fehlt im Paket: $sourceExe"
}
if (-not $AllowedCidrs -or $AllowedCidrs.Count -eq 0) {
    throw 'Mindestens ein vertrauenswürdiges Netzwerk muss mit -AllowedCidrs angegeben werden.'
}

if (-not $Install -and -not $Repair) {
    Write-Host 'Paketprüfung erfolgreich. Es wurden keine Systemänderungen vorgenommen.'
    Write-Host 'Installation als Administrator mit -Install starten; bestehende Installation mit -Repair aktualisieren.'
    return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Die Installation muss in einer PowerShell als Administrator ausgeführt werden.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Install -and $service) {
    throw "Der Dienst $ServiceName existiert bereits. Verwenden Sie -Repair."
}
if ($Repair -and -not $service) {
    throw "Der Dienst $ServiceName existiert nicht. Verwenden Sie -Install."
}

$serviceAccount = "NT SERVICE\$ServiceName"
$binaryPath = Join-Path $InstallRoot 'NovaNein.Server.exe'
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$firewallRule = "$ServiceName Webcockpit"

if ($PSCmdlet.ShouldProcess($InstallRoot, 'NovaNein-Programmdateien installieren')) {
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot, $DataRoot | Out-Null
    Copy-Item -Path (Join-Path $sourceApp '*') -Destination $InstallRoot -Recurse -Force

    foreach ($relative in @('db', 'documents', 'packages', 'datev-xsds', 'backups', 'datev-bridge')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $DataRoot $relative) | Out-Null
    }

    $productionConfig = [ordered]@{
        Storage = [ordered]@{
            DatabasePath = (Join-Path $DataRoot 'db\novanein.db')
            DocumentRoot = (Join-Path $DataRoot 'documents')
        }
        Cockpit = [ordered]@{
            DefaultLookbackDays = 90
            CuratedDocuments = @()
            CuratedDocumentsAlwaysIncludeCreatedFrom = ''
        }
        Datev = [ordered]@{
            PackageDirectory = (Join-Path $DataRoot 'packages')
            XsdPaths = @(
                (Join-Path $DataRoot 'datev-xsds\Document_v060.xsd'),
                (Join-Path $DataRoot 'datev-xsds\Document_types_v060.xsd'),
                (Join-Path $DataRoot 'datev-xsds\Belegverwaltung_online_invoice_v060.xsd'),
                (Join-Path $DataRoot 'datev-xsds\Belegverwaltung_online_types_v060.xsd')
            )
            RequireXsdValidation = $true
            AutoPreparePackages = $false
            AllowDirectTransfer = $false
            TransferAgentEnabled = $false
            TransferMode = 'Disabled'
            BackfillApprovedOnStartup = $false
        }
        Backup = [ordered]@{
            Directory = (Join-Path $DataRoot 'backups')
            RetentionDays = 30
        }
        OpenAI = [ordered]@{
            DocumentInterpretationModel = 'gpt-5.6'
            DocumentInterpretationReasoningEffort = 'high'
            DocumentInterpretationTimeoutSeconds = 180
            DocumentInterpretationMaximumPdfBytes = 20971520
            DocumentInterpretationMaximumOutputTokens = 16000
            DocumentInterpretationPdfDetail = 'high'
        }
        WebAccess = [ordered]@{
            Mode = 'TrustedNetwork'
            AllowedCidrs = @($AllowedCidrs)
            EnableSignalR = $true
        }
    }
    $configPath = Join-Path $InstallRoot 'appsettings.Production.json'
    $productionConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8

    & icacls.exe $DataRoot /inheritance:e /grant:r "$($serviceAccount):(OI)(CI)M" 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Berechtigungen für $DataRoot konnten nicht gesetzt werden." }

    if (-not $service) {
        New-Service -Name $ServiceName -BinaryPathName ('"' + $binaryPath + '"') -DisplayName 'NovaNein Beleg-Cockpit' -Description 'SAP-Belegprüfung und DATEV-Paketvorbereitung' -StartupType Automatic | Out-Null
    }
    & sc.exe config $ServiceName obj= $serviceAccount password= '' start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Dienstkonto für $ServiceName konnte nicht konfiguriert werden." }

    $environment = @(
        'ASPNETCORE_ENVIRONMENT=Production',
        ('ASPNETCORE_URLS=http://{0}:{1}' -f $ListenAddress, $Port)
    )
    New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    $protector = Join-Path $PackageRoot 'scripts\Protect-NovaNein-ServiceRegistry.ps1'
    if (Test-Path -LiteralPath $protector -PathType Leaf) {
        & $protector -ServiceName $ServiceName
    }

    Remove-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress $AllowedCidrs -Profile Private,Domain | Out-Null
}

if ($Start -and $PSCmdlet.ShouldProcess($ServiceName, 'Dienst starten')) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

Write-Host "NovaNein wurde unter $InstallRoot installiert."
Write-Host "Laufzeitdaten liegen unter $DataRoot."
Write-Host "Webcockpit: http://<server-ip>:$Port/"
Write-Host 'SAP-Zugang und OpenAI-Key müssen anschließend mit den mitgelieferten Set-NovaNein-Skripten sicher hinterlegt werden.'
