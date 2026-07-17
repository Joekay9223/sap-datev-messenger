[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][string]$CertificateThumbprint,
  [string]$ServerApplicationPath = 'C:\NovaNein\staging\NovaNein.Server.dll'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ServerApplicationPath)) { throw "Der NovaNein-Serverdienst wurde nicht gefunden: $ServerApplicationPath" }
if ($PSCmdlet.ShouldProcess($CertificateThumbprint, 'Arbeitsplatzzertifikat zentral sperren')) {
  & dotnet $ServerApplicationPath --revoke-workstation $CertificateThumbprint
  if ($LASTEXITCODE -ne 0) { throw 'Arbeitsplatzzertifikat konnte nicht gesperrt werden.' }
}
