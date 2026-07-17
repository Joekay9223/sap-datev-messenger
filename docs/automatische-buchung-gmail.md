# Automatische Buchung aus Gmail

## Sicherheitszustand nach Installation

Die Funktion wird grundsätzlich **deaktiviert** ausgeliefert. Die Kill-Switches müssen nach der Abnahme einzeln gesetzt werden:

```json
{
  "Gmail": { "Enabled": true },
  "Sap": {
    "Mode": "write-enabled",
    "EnableAttachments2Writes": true,
    "EnablePurchaseInvoiceWrites": true,
    "EnableBusinessPartnerWrites": false
  },
  "Datev": {
    "AutoPreparePackages": true,
    "TransferAgentEnabled": false,
    "AutoTransferApprovedInvoices": false,
    "AutoTransferGreenOnly": false,
    "AutoTransferNotBeforeUtc": ""
  }
}
```

`EnableBusinessPartnerWrites` wird erst nach einem separaten Stammdatentest aktiviert. DATEV-Pakete werden erst nach vollständigem SAP-Readback vorbereitet. In einen DATEV-Watchfolder gelangen ausschließlich originale PDFs oder das geprüfte geschlossene Drei-Dateien-ZIP.

## Einmalfreigabe und automatische DATEV-Übertragung

Nach abgeschlossener Abnahme kann NovaNein so betrieben werden, dass eine fachliche Freigabe den gesamten nachfolgenden technischen Prozess autorisiert:

- Bei einer automatischen Eingangsrechnung bestätigt der Benutzer genau einmal **„Freigeben und buchen“**.
- Bei einem bereits manuell in SAP gebuchten Beleg bestätigt der Benutzer genau einmal die Zuordnung beziehungsweise Prüfung der Original-PDF im Beleg-Cockpit.
- Danach erstellt NovaNein das DATEV-Paket und übergibt es ohne zusätzliche Aktion **„Übertragung bestätigen“**.
- Gutschriften behalten ihre eigene fachliche Gutschriftenfreigabe. Nach dieser Freigabe ist ebenfalls keine zweite Transferfreigabe erforderlich.

Für diesen Betrieb werden nach erfolgreicher Testabnahme gesetzt:

```json
{
  "Datev": {
    "AutoPreparePackages": true,
    "TransferAgentEnabled": true,
    "TransferMode": "LocalBridge",
    "AutoTransferApprovedInvoices": true,
    "AutoTransferGreenOnly": false,
    "AutoTransferNotBeforeUtc": "2026-07-16T16:45:00Z"
  }
}
```

`AutoTransferNotBeforeUtc` ist eine zwingende Sicherheitsgrenze und muss auf den tatsächlichen Aktivierungszeitpunkt der jeweiligen Installation gesetzt werden. Ohne gültigen Zeitpunkt bleibt die automatische Einreihung gesperrt. Beim Dienststart werden ausschließlich fachlich freigegebene Pakete berücksichtigt, deren Vorbereitungszeitpunkt an oder nach dieser Grenze liegt. Dadurch werden ältere, bereits vorhandene Pakete nicht ungeprüft rückwirkend übertragen.

`AutoTransferGreenOnly=true` ist die strengere Alternative, wenn ausschließlich grüne Vorgänge automatisch übertragen werden sollen. `AutoTransferApprovedInvoices=true` überträgt alle fachlich freigegebenen Rechnungen nach erfolgreicher Paketvalidierung.

## Weitergeleitete Ausgangsrechnungen ohne Human in the Loop

Eine an `invoices@example.invalid` weitergeleitete Ausgangsrechnung kann vollständig automatisch verarbeitet werden. Dabei wird **keine neue Ausgangsrechnung in SAP angelegt**. NovaNein akzeptiert ausschließlich eine bereits vorhandene SAP-Ausgangsrechnung und liest deren vollständige Buchungsdaten erneut.

Der automatische Pfad läuft nur, wenn alle folgenden Prüfungen grün sind:

- Example Company ist im PDF eindeutig Rechnungsaussteller;
- die sichtbare Rechnungsnummer führt zu genau einer vorhandenen SAP-Ausgangsrechnung;
- Kunde, Rechnungsdatum, Währung, Netto, Steuer und Brutto stimmen zwischen PDF und SAP überein;
- Journal, Kontierung, Steuerzeilen und AVT1-/DATEV-Zuordnungen sind vollständig;
- die Original-PDF ist noch nicht mit einer abweichenden PDF im Beleg-Cockpit oder SAP kollidiert;
- SAP-Attachments2, DATEV-Paketbildung und LocalBridge-Transfer sind vollständig freigegeben.

Erst nach diesem Readback wird die PDF an SAP angehängt, im Beleg-Cockpit grün verknüpft, als `Ausgangsrechnung-<DocNum>.zip` vorbereitet und über die DATEV-Bridge in `Rechnungsausgang` übertragen. Bei jeder Abweichung bleibt der Vorgang gesperrt; es erfolgt insbesondere **kein** SAP-Schreibzugriff vor dem vollständigen PDF-/SAP-Abgleich.

Die Aktivierung besitzt eine eigene Vorwärtsgrenze und verarbeitet keine älteren Vorschläge rückwirkend:

```json
{
  "Gmail": {
    "AutoProcessOutgoingInvoices": true,
    "AutoProcessOutgoingNotBeforeUtc": "2026-07-17T10:00:00Z"
  },
  "Sap": {
    "Mode": "write-enabled",
    "EnableAttachments2Writes": true,
    "AutoAttachApprovedDocuments": true
  },
  "Datev": {
    "AutoPreparePackages": true,
    "TransferAgentEnabled": true,
    "TransferMode": "LocalBridge",
    "AutoTransferApprovedInvoices": true,
    "AutoTransferNotBeforeUtc": "2026-07-17T10:00:00Z"
  }
}
```

Fehlt eines dieser Gates oder ein gültiger UTC-Aktivierungszeitpunkt, verarbeitet der Hintergrunddienst keinen Ausgangsrechnungsvorschlag automatisch.

## Gmail einrichten

1. Google-Workspace-OAuth-Client für das konfigurierte Beispielpostfach `invoices@example.invalid` anlegen.
2. Einmalige Benutzerfreigabe ausschließlich mit `https://www.googleapis.com/auth/gmail.modify` durchführen.
3. Topic und Pull-Subscription anlegen und Gmail die Veröffentlichungsberechtigung auf dem Topic erteilen.
4. Ein separates Dienstkonto für Pub/Sub anlegen und ausschließlich auf der einzelnen Pull-Subscription mit `roles/pubsub.subscriber` berechtigen.
5. Den JSON-Schlüssel des Dienstkontos außerhalb des Deployments ablegen. Auf dem Windows-Server wird `C:\ProgramData\NovaNein\Server\secrets\pubsub-service-account.json` verwendet; leseberechtigt sind ausschließlich `SYSTEM` und `NT SERVICE\NovaNein-Staging`.
6. Client-ID, Client-Secret und Refresh-Token auf dem Dienstkonto ausführen:

   ```powershell
   .\scripts\Set-NovaNein-GmailCredential.ps1
   ```

7. `Gmail:PubSubTopic` im Format `projects/<projekt>/topics/<topic>`, `Gmail:PubSubSubscription` im Format `projects/<projekt>/subscriptions/<subscription>` und `Gmail:PubSubServiceAccountCredentialPath` mit dem absoluten Schlüsselpfad konfigurieren.

Mit `Gmail:BackfillOnFirstSync=false` setzt NovaNein beim ersten Start nur einen aktuellen History-Anker. Historische Nachrichten werden dadurch nicht ungeplant vollständig importiert. Ein historischer Backfill muss ausdrücklich aktiviert werden.

NovaNein erneuert den Gmail-Watch spätestens vor Ablauf, verarbeitet Änderungen über `history.list` und fällt bei einer später abgelaufenen History-ID kontrolliert auf einen Vollabgleich zurück. Es archiviert und löscht keine E-Mails.

## SAP vorbereiten

Auf `OPCH` müssen vor Aktivierung der Eingangsrechnungswrites zwei alphanumerische UDFs vorhanden sein:

- `U_NN_ProposalId`, mindestens 36 Zeichen
- `U_NN_SourceHash`, mindestens 64 Zeichen

Die Felder werden in SAP Business One über **Werkzeuge → Anpassungswerkzeuge → Benutzerdefinierte Felder** auf Einkaufsbelegen angelegt. Vor Produktivfreigabe sind die tatsächlichen Service-Layer-Feldnamen per GET zu bestätigen.

`Sap:AttachmentSourceRoot` muss auf den von SAP erreichbaren und für NovaNein freigegebenen PDF-Quellpfad zeigen. Konten, die Kostenstellen oder Dimensionen verlangen, werden unter `Sap:AccountsRequiringDimensions` eingetragen und dadurch für automatische Buchungen gesperrt.

## Rollen und Ablauf

- `Reviewer`: Vorschläge bearbeiten, begründet ablehnen oder „Freigeben und buchen“ auslösen.
- `MasterDataApprover`: neuen Lieferanten separat freigeben und in SAP anlegen.
- `Admin`: Gmail-Synchronisierung und Integrationsschalter verwalten.

Jede Freigabe enthält die geladene Vorschlagsversion. Zwischenzeitliche Änderungen führen zu einem Konflikt statt zu einer Buchung. Nach einem unklaren SAP-Timeout wird ausschließlich über `U_NN_ProposalId` geprüft; NovaNein sendet den POST nicht blind erneut.

## Abnahmereihenfolge

1. Gmail-Testmail mit PDF und Dublettenwiederholung.
2. Eindeutiger Lieferant und reine EUR-Kostenrechnung.
3. Stammdatenvorschlag bei unbekanntem Lieferanten.
4. Waren-, Bestell-, Gutschrift-, Reverse-Charge-, Fremdwährungs- und dimensionspflichtige Fälle müssen rot blockieren.
5. SAP-Testfirma: Attachment, PurchaseInvoice, UDFs, `DocEntry`, `DocNum`, `TransId`, Journal-, Steuer- und AVT1-Readback.
6. Isoliertes DATEV-Testziel: XSD-validiertes Drei-Dateien-ZIP, danach `UploadSucceeded` und `JobFinalized`.
7. Erst danach automatische DATEV-Übertragung mit einem neuen `AutoTransferNotBeforeUtc` aktivieren und prüfen, dass kein älteres Paket eingereiht wird.

Eine Aktivierung erfolgt erst nach einem reproduzierbaren Test gegen eine isolierte Testumgebung und einer ausdrücklichen Betreiberfreigabe.
