[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$CertificateDirectory = 'C:\NovaNein\staging\certs',
  [string[]]$ServerIpAddresses = @()
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }
if ($PSCmdlet.ShouldProcess($CertificateDirectory, 'Staging-Zertifikatskette erzeugen und lokal vertrauen')) {
  New-Item -ItemType Directory -Force $CertificateDirectory | Out-Null
  $root = Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { $_.FriendlyName -eq 'NovaNein Staging Root CA' -and $_.HasPrivateKey } | Select-Object -First 1
  if (-not $root) {
    $root = New-SelfSignedCertificate -Type Custom -Subject 'CN=NovaNein Staging Root CA' -CertStoreLocation 'Cert:\CurrentUser\My' -FriendlyName 'NovaNein Staging Root CA' -KeyUsage CertSign,CRLSign,DigitalSignature -KeyExportPolicy NonExportable -TextExtension @('2.5.29.19={critical}{text}ca=true')
  }
  $rootPath = Join-Path $CertificateDirectory 'novanein-staging-root-ca.cer'
  Export-Certificate -Cert $root -FilePath $rootPath -Force | Out-Null
  & certutil.exe -addstore Root $rootPath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Die Staging-Root-CA konnte nicht im lokalen Vertrauensspeicher eingerichtet werden. Das Skript muss als lokaler Administrator laufen.' }

  foreach ($ip in $ServerIpAddresses) { $parsedIp = $null; if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsedIp)) { throw "Ungültige Server-IP-Adresse: $ip" } }
  $sanValues = @('DNS=localhost', 'DNS=example-sap-host') + ($ServerIpAddresses | ForEach-Object { "IPAddress=$_" })
  $server = New-SelfSignedCertificate -Type Custom -Signer $root -CertStoreLocation 'Cert:\CurrentUser\My' -FriendlyName 'NovaNein Staging Server' -KeyUsage DigitalSignature,KeyEncipherment -KeyExportPolicy NonExportable -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1', ('2.5.29.17={text}' + ($sanValues -join '&')))
  $client = New-SelfSignedCertificate -Type Custom -Subject 'CN=NovaNein Staging Client' -Signer $root -CertStoreLocation 'Cert:\CurrentUser\My' -FriendlyName 'NovaNein Staging Client' -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2')
  [pscustomobject]@{RootThumbprint=$root.Thumbprint;ServerThumbprint=$server.Thumbprint;ClientThumbprint=$client.Thumbprint;RootCertificate=$rootPath;ServerCertificateStore=$server.PSPath;ClientCertificateStore=$client.PSPath}
}
