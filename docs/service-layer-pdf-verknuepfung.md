# PDF-Beleg direkt mit einer SAP-Rechnung verknüpfen

## Ergebnis

Ja. SAP Business One stellt über den Service Layer das Medienobjekt `Attachments2` bereit. Eine Ausgangsrechnung (`Invoices`) oder Eingangsrechnung (`PurchaseInvoices`) besitzt das Feld `AttachmentEntry`. Dadurch kann ein PDF technisch eindeutig mit dem unveränderlichen SAP-Schlüssel `DocEntry` verknüpft werden.

Der Service Layer ist allerdings nur die API. Für Drag-and-drop direkt in der SAP-Oberfläche wird entweder die native SAP-Anhangsfunktion oder ein sehr kleines SAP-UI-Add-on benötigt.

## Empfohlener Ablauf für bereits manuell gebuchte Rechnungen

1. Benutzer öffnet in SAP genau die gewünschte Rechnung.
2. Ein kleines Add-on übernimmt `ObjType`, `DocEntry`, `DocNum`, Geschäftspartner und Betrag aus dem aktiven Formular.
3. Benutzer zieht das PDF in eine Drop-Zone; PDF wird angezeigt und SHA-256 berechnet.
4. Besteht noch kein `AttachmentEntry`, wird das PDF per `POST /b1s/v2/Attachments2` als `multipart/form-data` hochgeladen.
5. Der zurückgegebene `AbsoluteEntry` wird per `PATCH /b1s/v2/Invoices(<DocEntry>)` oder `PATCH /b1s/v2/PurchaseInvoices(<DocEntry>)` als `AttachmentEntry` gesetzt.
6. Besteht bereits ein `AttachmentEntry`, wird die neue Datei per `PATCH /b1s/v2/Attachments2(<AttachmentEntry>)` an dieselbe Anhangssammlung angehängt. Der vorhandene Verweis darf nicht überschrieben werden.
7. Das System liest Rechnung und `Attachments2` erneut, lädt das PDF testweise zurück und vergleicht Dateigröße sowie SHA-256.
8. Erst nach diesem Readback entsteht der unveränderliche Korrelationsschlüssel `EntitySet + DocEntry + AttachmentEntry + SHA-256`.
9. Anschließend darf der DATEV-Paketauftrag erzeugt werden.

Beispiel für die Belegverknüpfung:

```http
PATCH /b1s/v2/PurchaseInvoices(4711)
Content-Type: application/json

{
  "AttachmentEntry": 8123
}
```

Für eine Ausgangsrechnung wird entsprechend `Invoices(4711)` verwendet.

## Neue Rechnungen über den Service Layer

Wird später auch die Rechnung selbst über die API angelegt:

1. PDF zuerst als `Attachments2` hochladen.
2. `AbsoluteEntry` aus der Antwort übernehmen.
3. Rechnung mit `AttachmentEntry` im `POST Invoices` bzw. `POST PurchaseInvoices` anlegen.
4. Rechnung und Anhang vollständig zurücklesen und prüfen.

Upload und Rechnungserstellung sind weiterhin zwei technische Schritte. Die Anwendung muss deshalb idempotent arbeiten und verwaiste Uploads erkennen.

## Ausgangsrechnungen

Der Service Layer rendert nicht automatisch ein SAP-/Crystal-/Coresuite-Layout als PDF. Die PDF-Erzeugung bleibt ein eigener Schritt:

- PDF über das freigegebene SAP-Ausgangslayout erzeugen;
- SHA-256 berechnen;
- über `Attachments2` hochladen;
- mit `Invoices(<DocEntry>)` verknüpfen;
- danach DATEV-Paket erzeugen.

Ein Add-on kann auf das erfolgreiche Hinzufügen einer Ausgangsrechnung reagieren und diesen Ablauf automatisch anstoßen. Alternativ kann ein externer Dienst neue Rechnungen über den Service Layer erkennen.

## Bedeutung von „100 % Confidence“

Die technische Zuordnung ist eindeutig, weil nicht über Rechnungsnummer, Dateiname oder Volltext gesucht wird, sondern über:

- SAP-Objektart;
- unveränderlichen `DocEntry`;
- SAP-`AttachmentEntry`;
- Hash des Originaldokuments.

Das garantiert, **welches PDF an welchem SAP-Beleg hängt**. Es garantiert nicht automatisch, dass ein Benutzer inhaltlich das richtige PDF ausgewählt hat. Für Eingangsrechnungen bleiben deshalb Vorschau und Plausibilitätsprüfung gegen Lieferant, Fremdbelegnummer, Datum, Währung und Betrag erforderlich.

## Sicherheitsregeln

- Kein Überschreiben eines vorhandenen `AttachmentEntry`.
- Gleichnamige Dateien ohne ausdrückliche Versionierung nicht überschreiben.
- Originaldatei unverändert erhalten; OCR-/Vorschaudateien getrennt speichern.
- PDF-Typ, Dateigröße und Malware prüfen.
- Upload, Link, Readback und Hashprüfung vollständig auditieren.
- DATEV-Erzeugung erst nach erfolgreichem Readback starten.
- Fehler zwischen Upload und SAP-PATCH als kompensierbaren Zwischenzustand speichern.
- Bei bereits geschlossenem oder gebuchtem Dokument die PATCH-Berechtigung in einer Testfirma für die konkrete SAP-Version prüfen.

## Quellen

- [SAP: Stream Entity Upload für Attachments2](https://help.sap.com/docs/SAP_BUSINESS_ONE/f110a154dd0f4c20bf7f3ebca9eeb794/fbdaa0c91cde4421981862112d24178a.html?version=10.0)
- [SAP: Upload zu einem entfernten Service Layer](https://help.sap.com/docs/SAP_BUSINESS_ONE/f110a154dd0f4c20bf7f3ebca9eeb794/da1993a262dc44ceaa72253e4b920376.html)
- [SAP: Attachment aktualisieren oder weitere Datei anhängen](https://help.sap.com/docs/SAP_BUSINESS_ONE/f110a154dd0f4c20bf7f3ebca9eeb794/c7c0b0e7f1c8435da9436ad618be844a.html?locale=en-US&state=PRODUCTION&version=10.0)
- [SAP SDK: AttachmentEntry am Documents-Objekt](https://help.sap.com/doc/089315d8d0f8475a9fc84fb919b501a3/10.0/en-US/SDKHelp/SAPbobsCOM~Documents_members.html)
