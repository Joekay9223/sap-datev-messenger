[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$ServiceName = 'NovaNein-Staging',
  [Parameter(Mandatory)][string]$SapEndpoint,
  [Parameter(Mandatory)][string]$SapCompanyDatabase,
  [Parameter(Mandatory)][string]$SapUserName,
  [Parameter(Mandatory)][SecureString]$SapPassword,
  [SecureString]$OpenAiApiKey,
  [switch]$TrustLocalSapCertificate,
  [string]$OpenAiModel = 'gpt-5.6-terra'
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Dieses Skript muss als Administrator auf dem SAP-Server ausgeführt werden.' }
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { throw "Der Dienst $ServiceName wurde nicht gefunden." }
$parsedEndpoint = $null
if (-not ([Uri]::TryCreate($SapEndpoint, [UriKind]::Absolute, [ref]$parsedEndpoint)) -or -not $SapEndpoint.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw 'Der SAP-Service-Layer-Endpunkt muss eine absolute HTTPS-Adresse sein.' }
$serviceLayerEndpoint = $parsedEndpoint.AbsoluteUri.TrimEnd('/')
if (-not $serviceLayerEndpoint.EndsWith('/b1s/v2', [StringComparison]::OrdinalIgnoreCase)) { $serviceLayerEndpoint += '/b1s/v2' }

function ConvertFrom-SecureStringPlain([SecureString]$Value) {
  $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$values = @{
  'Sap__Endpoint' = $serviceLayerEndpoint + '/'
  'Sap__CompanyDatabase' = $SapCompanyDatabase.Trim()
  'Sap__UserName' = $SapUserName.Trim()
  'Sap__Password' = ConvertFrom-SecureStringPlain $SapPassword
  'OpenAI__Model' = $OpenAiModel.Trim()
}
if ($TrustLocalSapCertificate) { $values['Sap__TrustServerCertificate'] = 'true' }
if ($OpenAiApiKey) { $values['OpenAI__ApiKey'] = ConvertFrom-SecureStringPlain $OpenAiApiKey }
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$existing = @((Get-ItemProperty -Path $key -Name Environment -ErrorAction SilentlyContinue).Environment)
$kept = $existing | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Where-Object { $name = $_.Split('=', 2)[0]; -not $values.ContainsKey($name) }
$updated = @($kept) + @($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
if ($PSCmdlet.ShouldProcess($ServiceName, 'SAP- und OpenAI-Secrets im geschützten Dienstkontext speichern und Dienst neu starten')) {
  & (Join-Path $PSScriptRoot 'Protect-NovaNein-ServiceRegistry.ps1') -ServiceName $ServiceName
  New-ItemProperty -Path $key -Name Environment -Value $updated -PropertyType MultiString -Force | Out-Null
  Restart-Service -Name $ServiceName -Force
  Write-Output 'Secrets wurden für den Dienst gespeichert und der Dienst wurde neu gestartet. Die Klartextwerte werden nicht ausgegeben.'
}
