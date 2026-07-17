[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$PfxPath,
  [Parameter(Mandatory)][SecureString]$PfxPassword,
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$RootCertificatePath
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }

function Get-PrivateKeyPath([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
  $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
  try {
    if ($rsa -is [System.Security.Cryptography.RSACng]) {
      $path = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName)
    }
    elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
      $path = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName)
    }
    else { throw 'Der Clientzertifikatsschlüssel verwendet keinen unterstützten RSA-Schlüsselanbieter.' }
    if (-not (Test-Path -LiteralPath $path)) { throw "Die private Schlüsseldatei wurde nicht gefunden: $path" }
    return $path
  }
  finally { if ($rsa) { $rsa.Dispose() } }
}

if ($PSCmdlet.ShouldProcess('Lokaler Computer', 'NovaNein-Root- und Arbeitsplatzzertifikat installieren')) {
  & certutil.exe -addstore Root $RootCertificatePath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Das NovaNein-Root-Zertifikat konnte nicht im lokalen Vertrauensspeicher installiert werden. Als lokaler Administrator ausführen.' }
  $certificate = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation 'Cert:\LocalMachine\My' -Password $PfxPassword -Exportable:$false
  if (-not $certificate.HasPrivateKey) { throw 'Das importierte Arbeitsplatzzertifikat besitzt keinen privaten Schlüssel.' }
  $clientAuthenticationOid = '1.3.6.1.5.5.7.3.2'
  $ekuExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
  $enhancedUsages = if ($ekuExtension) { (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($ekuExtension, $ekuExtension.Critical)).EnhancedKeyUsages | ForEach-Object { $_.Value } } else { @() }
  if (-not ($enhancedUsages -contains $clientAuthenticationOid)) { throw 'Das importierte Zertifikat ist nicht für Clientauthentifizierung ausgestellt.' }
  $privateKeyPath = Get-PrivateKeyPath $certificate
  & icacls.exe $privateKeyPath /grant '*S-1-5-32-545:R' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Der interaktiven Benutzergruppe konnte kein Lesezugriff auf den Clientzertifikatsschlüssel gewährt werden.' }
  [pscustomobject]@{
    CertificateThumbprint = $certificate.Thumbprint
    CertificateStore = $certificate.PSPath
    PrivateKeyPath = $privateKeyPath
    NextStep = "Thumbprint mit dem NovaNein-Serverbefehl --register-workstation registrieren und PFX-Datei anschließend löschen."
  }
}
