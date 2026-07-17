[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$WorkstationName,
  [Parameter(Mandatory)][SecureString]$PfxPassword,
  [string]$ProvisioningDirectory = 'C:\NovaNein\provisioning',
  [string]$OutputDirectory = 'C:\NovaNein\client-packages',
  [string]$RootCertificatePath = 'C:\NovaNein\staging\certs\novanein-staging-root-ca.cer',
  [string]$ClientPayloadDirectory = 'C:\NovaNein\staging\client-template',
  [Parameter(Mandatory)][ValidatePattern('^https://')][string]$ServerUrl
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }
if (-not (Test-Path -LiteralPath $RootCertificatePath)) { throw "Das Root-Zertifikat fehlt: $RootCertificatePath" }
$safeNames = @($WorkstationName | ForEach-Object {
  $value = $_.Trim()
  if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,62}$') { throw "Ungültiger Arbeitsplatzname: $value" }
  $value
})
if ($safeNames.Count -ne @($safeNames | Sort-Object -Unique).Count) { throw 'Arbeitsplatznamen müssen innerhalb eines Rollouts eindeutig sein.' }

$certificateScript = Join-Path $PSScriptRoot 'new-novanein-workstation-certificate.ps1'
$packageScript = Join-Path $PSScriptRoot 'Build-NovaNein-ClientPackage.ps1'
$created = [System.Collections.Generic.List[object]]::new()
foreach ($safeName in $safeNames) {
  $pfxPath = Join-Path $ProvisioningDirectory "novanein-$safeName.pfx"
  if ($PSCmdlet.ShouldProcess($safeName, 'Individuelles NovaNein-Clientpaket erstellen und zentral registrieren')) {
    $certificate = & $certificateScript -WorkstationName $safeName -PfxPassword $PfxPassword -OutputDirectory $ProvisioningDirectory -RegisterWithServer
    try {
      $package = & $packageScript -WorkstationName $safeName -PfxPath $pfxPath -RootCertificatePath $RootCertificatePath -ServerUrl $ServerUrl -ClientPayloadDirectory $ClientPayloadDirectory -OutputDirectory $OutputDirectory
      $created.Add([pscustomobject]@{ WorkstationName = $safeName; CertificateThumbprint = $certificate.CertificateThumbprint; Package = $package.FullName })
    }
    finally {
      Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
    }
  }
}
$created
