[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{1,62}$')][string]$WorkstationName,
  [Parameter(Mandatory)][SecureString]$PfxPassword,
  [string]$OutputDirectory = 'C:\NovaNein\provisioning',
  [switch]$RegisterWithServer,
  [switch]$KeepIssuedCertificate,
  [string]$ServerApplicationPath = 'C:\NovaNein\staging\NovaNein.Server.dll'
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Nur unter Windows ausführen.' }
$root = Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { $_.FriendlyName -eq 'NovaNein Staging Root CA' -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $root) { throw 'Die NovaNein-Root-CA fehlt. Zuerst new-novanein-staging-certificates.ps1 auf dem Server ausführen.' }

$safeName = $WorkstationName.Trim()
$target = Join-Path $OutputDirectory ("novanein-{0}.pfx" -f $safeName)
if ($PSCmdlet.ShouldProcess($target, "Clientzertifikat für $safeName erzeugen")) {
  New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
  & icacls.exe $OutputDirectory /inheritance:r /grant:r "$env:USERNAME`:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Die Berechtigungen des Provisionierungsordners konnten nicht eingeschränkt werden.' }
  if (Test-Path -LiteralPath $target) { throw "Die Provisionierungsdatei existiert bereits: $target" }
  $certificate = New-SelfSignedCertificate -Type Custom -Subject ("CN=NovaNein {0}" -f $safeName) -Signer $root -CertStoreLocation 'Cert:\CurrentUser\My' -FriendlyName ("NovaNein Client {0}" -f $safeName) -KeyUsage DigitalSignature -KeyExportPolicy Exportable -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2')
  try {
    Export-PfxCertificate -Cert $certificate -FilePath $target -Password $PfxPassword -NoProperties | Out-Null
    if ($RegisterWithServer) {
      if (-not (Test-Path -LiteralPath $ServerApplicationPath)) { throw "Der NovaNein-Serverdienst wurde nicht gefunden: $ServerApplicationPath" }
      $dotnetCandidates = @(
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
        (Join-Path $HOME '.dotnet-sdk\dotnet.exe'),
        $(try { (Get-Command dotnet -ErrorAction Stop).Source } catch { $null })
      ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
      $dotnet = $dotnetCandidates | Select-Object -First 1
      if (-not $dotnet) { throw 'Für die zentrale Zertifikatsregistrierung wurde kein .NET-Host gefunden.' }
      & $dotnet $ServerApplicationPath --register-workstation $certificate.Thumbprint $safeName | Out-Null
      if ($LASTEXITCODE -ne 0) { throw 'Das Arbeitsplatzzertifikat konnte nicht zentral registriert werden.' }
    }
    $result = [pscustomobject]@{
      WorkstationName = $safeName
      CertificateThumbprint = $certificate.Thumbprint
      ProvisioningPfx = $target
      NextStep = if ($RegisterWithServer) { 'PFX und Root-Zertifikat mit dem Client-Installer auf dem Zielarbeitsplatz installieren.' } else { 'Auf dem Zielarbeitsplatz installieren und anschließend zentral mit --register-workstation registrieren.' }
    }
    if (-not $KeepIssuedCertificate) { Remove-Item -LiteralPath $certificate.PSPath -Force }
    $result
  }
  catch {
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    Remove-Item -LiteralPath $certificate.PSPath -Force -ErrorAction SilentlyContinue
    throw
  }
}
