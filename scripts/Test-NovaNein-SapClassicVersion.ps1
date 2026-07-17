[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Version,
  [string]$PreviousRegisteredVersion
)

$ErrorActionPreference = 'Stop'

function ConvertTo-SapAddonComparisonValue([string]$Value, [string]$Name) {
  if ($Value -notmatch '^\d+(?:\.\d+){3}$') {
    throw "$Name muss für NovaNein aus genau vier durch Punkte getrennten Zahlenblöcken bestehen, zum Beispiel 1.1.0.0."
  }

  # SAP Business One ignoriert beim Versionsvergleich die Punkte vollständig.
  # 1.1.0.0 wird daher als 1100 und 1.0.0.7 als 1007 verglichen.
  return [Numerics.BigInteger]::Parse(
    $Value.Replace('.', ''),
    [Globalization.CultureInfo]::InvariantCulture
  )
}

$comparisonValue = ConvertTo-SapAddonComparisonValue $Version 'Version'
$previousComparisonValue = $null
if ($PreviousRegisteredVersion) {
  $previousComparisonValue = ConvertTo-SapAddonComparisonValue $PreviousRegisteredVersion 'PreviousRegisteredVersion'
  if ($comparisonValue -le $previousComparisonValue) {
    throw "Die technische SAP-Version $Version wird als $comparisonValue verglichen und ist nicht größer als die registrierte Version $PreviousRegisteredVersion ($previousComparisonValue)."
  }
}

[pscustomobject]@{
  Version = $Version
  SapComparisonValue = $comparisonValue.ToString([Globalization.CultureInfo]::InvariantCulture)
  PreviousRegisteredVersion = $PreviousRegisteredVersion
  PreviousSapComparisonValue = if ($null -ne $previousComparisonValue) {
    $previousComparisonValue.ToString([Globalization.CultureInfo]::InvariantCulture)
  } else {
    $null
  }
  Result = 'gültig'
}
