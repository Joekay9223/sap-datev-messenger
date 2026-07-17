# Coresuite-PDF-Export

Der Ausgangsrechnungsprozess verwendet das vorhandene, freigegebene Coresuite-Layout. Das Rechnungsdesign wird nicht nachgebaut.

## Verifizierter API-Pfad

Auf dem EXAMPLE-SAP-Server ist Coresuite 7.45 installiert. Die dort vorhandene API stellt folgenden Aufruf bereit:

```csharp
var api = new CoresuiteServiceAPI.Designer.PrintAPI(config);
var result = api.GeneratePdf(documentKey, printDefinitionId);
```

`result` ist `swissLD.PrintHandlers.PrintResult` und enthält:

- `Success`
- `ErrorMessage`
- `ExportPath` – die erzeugten PDF-Pfade
- `Metadata`

## Geplanter Arbeitsplatzablauf

1. Das SAP-Add-on erkennt das erfolgreiche Speichern einer Ausgangsrechnung und liest `DocEntry`/`DocNum` aus dem aktuellen SAP-Kontext.
2. Es ruft Coresuite im Kontext der bestehenden SAP-Anmeldung mit der freigegebenen Print-Definition auf.
3. Es akzeptiert nur genau eine vorhandene PDF aus `ExportPath` und prüft die PDF-Signatur.
4. Es übermittelt PDF, `DocEntry`, `DocNum`, SAP-Benutzer und Rechnername per mTLS an den NovaNein-Server.
5. Der Server liest den Beleg erneut aus SAP und führt die gleiche Validierungs- und Dublettenlogik wie bei Eingangsrechnungen aus.

## Noch vor dem Produktiveinsatz erforderlich

- Die verbindliche Print-Definition für Ausgangsrechnungen auswählen und ihre ID dokumentieren.
- Den Aufruf mit einem **Staging-/Testbeleg** ausführen und prüfen, dass genau eine PDF entsteht.
- Sicherstellen, dass Coresuite keine zusätzliche E-Mail, Druckausgabe oder Buchungsänderung auslöst.
- Die Coresuite-Zugangsdaten nie in Dateien, Git oder die Server-API übernehmen; der Export läuft ausschließlich im SAP-Arbeitsplatzkontext.
