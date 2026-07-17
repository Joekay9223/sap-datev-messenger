# SAP-Add-on-Host

Der .NET-Framework-4.8-Host wird von SAP Business One mit dem Add-on-Verbindungsstring gestartet. Er verbindet sich über die registrierte `SAPbouiCOM.SboGuiApi`-COM-Komponente und beobachtet den aktiven Belegdialog.

Unterstützt werden derzeit:

- Ausgangsrechnung, Formtyp `133`, Datenquelle `OINV`;
- Eingangsrechnung, Formtyp `141`, Datenquelle `OPCH`.

Andere Formtypen, insbesondere Gutschriften, werden für Drag-and-drop noch nicht als aktiver Belegkontext übernommen. Der serverseitige PDF-Vollständigkeitsscan berücksichtigt Gutschriften unabhängig davon.

Der Host liest `DocEntry`, `DocNum` und den angezeigten SAP-Benutzer und übergibt sie an das schwebende NovaNein-Fenster. Authentifiziert ist gegenüber dem Server nur das mTLS-Arbeitsplatzzertifikat; der SAP-Benutzer bleibt im Audit ausdrücklich eine unverifizierte Angabe. Der Kontext wird regelmäßig aktualisiert und unmittelbar vor einem Drop erneut geprüft. Bei einem Formularwechsel oder geschlossenen Beleg wird die Übergabe deaktiviert.

Serveradresse und Zertifikatsthumbprint werden vorrangig aus `C:\ProgramData\NovaNein\client.config` gelesen. Diese individuelle Arbeitsplatzkonfiguration wird durch das Clientpaket installiert. Die Werte aus `NovaNein.SapAddonHost.exe.config` sind nur ein Fallback; die Konfiguration enthält keine SAP- oder OpenAI-Zugangsdaten.

Neben der PDF-Übergabe zeigt der Host Validierungsgründe, den begründeten gelben Review-Dialog, Montagsnotizen und den rein lesenden PDF-Anhangscan. Status und gelbe Review-Gründe werden beim erneuten Öffnen des SAP-Belegs serverseitig wiedergefunden. Eine geladene Notiz kann bis zur Ergänzung eines Mark-as-read-Endpunkts nach einem Neustart erneut erscheinen.

Der aktuelle Host ist Bestandteil des klassischen Pakets 1.0.0.6/v12. Paket und Prüfsummen sind validiert; die lokale SAP-Installation und Laufzeitabnahme stehen noch aus.
