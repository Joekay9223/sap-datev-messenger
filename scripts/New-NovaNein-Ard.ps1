[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ })][string]$PayloadDirectory,
  [Parameter(Mandatory)][string]$OutputPath,
  [string]$AddonName = 'NovaNein',
  [string]$Version = '1.0.0',
  [ValidateNotNullOrEmpty()][string]$ContactData = 'https://example.invalid/novanein'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PayloadDirectory 'NovaNein.SapAddonHost.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'NovaNein.SapAddonHost.exe fehlt im Add-on-Payload.' }
if ($PSCmdlet.ShouldProcess($OutputPath, 'SAP Business One Add-on Registration Data erzeugen')) {
  $outputDirectory = Split-Path -Parent $OutputPath; New-Item -ItemType Directory -Force $outputDirectory | Out-Null
  $signature = (Get-FileHash -Algorithm MD5 -LiteralPath $exe).Hash
  $settings = [Xml.XmlWriterSettings]::new(); $settings.Indent = $true; $settings.Encoding = [Text.UTF8Encoding]::new($false)
  $writer = [Xml.XmlWriter]::Create($OutputPath, $settings)
  try {
    $writer.WriteStartDocument(); $writer.WriteStartElement('AddOnRegData');
    $writer.WriteAttributeString('xmlns','xsd',$null,'http://www.w3.org/2001/XMLSchema'); $writer.WriteAttributeString('xmlns','xsi',$null,'http://www.w3.org/2001/XMLSchema-instance')
    # Diese Schema-3-Datei ist ausschließlich die Quelle für SAPs
    # ExtensionPackage.exe. Sie darf nicht als nackte ARD im klassischen
    # Dialog „Add-on registrieren“ ausgewählt werden.
    foreach($pair in @{ SlientInstallation='No'; SlientUpgrade='No'; Partnernmsp='NovaNein'; SchemaVersion='3.0'; Type='LightAddOn'; MultipleVersion='false'; OnDemand='True'; OnPremise='True'; ExtName=$AddonName; ExtVersion=$Version; Contdata=$ContactData; Partner='NovaNein'; DBType='MSSQL'; ClientType='W' }.GetEnumerator()) { $writer.WriteAttributeString($pair.Key, $pair.Value) }
    # Die SAP-Registrierung vergleicht gegen eine interne Buildnummer, die nicht
    # zuverlässig der in der EXE angezeigten Produktversion entspricht. Dieser
    # Bereich entspricht dem auf derselben Instanz akzeptierten regulären Novaline-Windows/MSSQL-Add-on.
    $writer.WriteStartElement('Validity'); $writer.WriteStartElement('SBOCompatibility'); $writer.WriteStartElement('Version'); $writer.WriteAttributeString('From','1000.000.00'); $writer.WriteAttributeString('To','1009.999.99'); $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteStartElement('Configuration'); foreach($element in 'Repository','Deployment','Assignment'){ $writer.WriteStartElement($element); $writer.WriteElementString('Properties',''); $writer.WriteEndElement() }; $writer.WriteEndElement()
    # SAP's A group means Automatic; M means Manual.
    $writer.WriteStartElement('Addons'); $writer.WriteStartElement('Addon'); foreach($pair in @{ Name=$AddonName; Group='A'; ForceFlag='False'; Visible='True'; AutoAssign='False'; SelfUpgrd='False' }.GetEnumerator()) { $writer.WriteAttributeString($pair.Key,$pair.Value) }
    $writer.WriteStartElement('x86'); $writer.WriteAttributeString('AddonExe',''); $writer.WriteAttributeString('AddonSig','');
    foreach($section in 'Installation','Uninstallation') { $writer.WriteStartElement($section); $writer.WriteStartElement('Files'); $writer.WriteEndElement(); $writer.WriteEndElement() }
    $writer.WriteEndElement()
    $writer.WriteStartElement('x64'); $writer.WriteAttributeString('AddonExe','NovaNein.SapAddonHost.exe'); $writer.WriteAttributeString('AddonSig',$signature); $writer.WriteAttributeString('ExeDir','X64Client')
    # Nur tatsächliche Laufzeitdateien registrieren. Logs, PDBs und sonstige Nebenartefakte
    # dürfen nicht als SAP-Add-on-Dateien in die ARD gelangen.
    $runtimeFiles = Get-ChildItem -LiteralPath $PayloadDirectory -File |
      Where-Object { $_.Extension -in '.exe','.dll','.config' } |
      Sort-Object Name
    foreach($section in 'Installation','Uninstallation') { $writer.WriteStartElement($section); $writer.WriteStartElement('Files'); $runtimeFiles | ForEach-Object { $writer.WriteStartElement('File'); $writer.WriteAttributeString('FileName',('X64Client\' + $_.Name)); $writer.WriteStartElement('Actions'); $writer.WriteEndElement(); $writer.WriteEndElement() }; $writer.WriteEndElement(); $writer.WriteEndElement() }
    $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteStartElement('XApps'); $writer.WriteStartElement('XApp'); $writer.WriteAttributeString('Name',''); $writer.WriteAttributeString('Path',''); $writer.WriteAttributeString('FileName',''); $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteStartElement('UDQs'); $writer.WriteStartElement('UDQ'); $writer.WriteAttributeString('udqname',''); $writer.WriteStartElement('Hana'); $writer.WriteAttributeString('FileName',''); $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteEndElement(); $writer.WriteEndDocument()
  }
  finally { $writer.Dispose() }
  Get-Item -LiteralPath $OutputPath
}
