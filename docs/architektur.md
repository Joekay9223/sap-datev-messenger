# Architektur

## Komponenten

| Komponente | Verantwortung | Berechtigung im Grundgerüst |
| --- | --- | --- |
| SAP-Leseadapter | Liest Eingangsrechnungen, Fremdbelegnummern, Steuer- und Referenzdaten. | Nur Lesen |
| Belegquellen-Adapter | Liefert Originalbelege und Provenienz. | Nur Lesen |
| Abgleich-Engine | Verknüpft SAP-Vorgang, Beleg, Archivpaket und Transfer-Evidenz. | Keine externen Schreibzugriffe |
| DATEV-Paketgenerator | Erstellt das geschlossene, valide Drei-Dateien-ZIP. | Arbeitsverzeichnis, nie direkt in den Watchfolder |
| Transfer-Adapter | Prüft Ziel und übergibt ausschließlich geprüfte PDFs bzw. geprüfte ZIPs. | Später explizit freizugeben |
| Audit-Protokoll | Speichert nachvollziehbar Prüfungen, Entscheidungen und Transfer-Evidenz. | Append-only anstreben |

## Vertrauensgrenzen

- **SAP:** Quelle für Buchungs- und Steuerdaten; ein Dokumentenanhang beweist keine DATEV-Übermittlung.
- **Belegquellen:** nur Originale oder eindeutig verifizierte Dokumente verwenden.
- **DATEV-Archiv:** belegt das abgelegte Paket, ersetzt aber nicht die Transfer-Evidenz.
- **BTTnext-Protokolle:** `UploadSucceeded` und `JobFinalized` bilden den belastbaren Transfernachweis.
- **Watchfolder:** ist eine Sicherheitsgrenze. Er darf keine losen Metadaten erhalten.

## Öffentliche Domänenschnittstellen

Das Grundgerüst stellt reine Funktionen bereit:

- `validateDatevPackage(entries)` prüft die verbindliche Drei-Dateien-Struktur.
- `validateWatchfolderCandidate(candidate)` blockiert unzulässige Watchfolder-Inhalte.
- `classifyTransferEvidence(evidence)` trennt SAP-/Archivhinweise von einem nachgewiesenen DATEV-Transfer.
- `deriveDatevBuCode(taxData)` übernimmt den aus SAP stammenden DATEV-Code; es gibt keinen Altarchiv-Fallback.

## Noch bewusst offen

Die produktive Lösung wird als Hybrid betrieben: ein vollständig interner externer Kern mit lokaler SQLite-Datenhaltung für Metadaten und Prozesszustände, einem internen dateibasierten Originalspeicher und einem dünnen schwebenden SAP-Business-One-UI-Add-on. DATEV Belegtransfer bleibt als installierter Transportbaustein bestehen und überwacht den von der Eigenlösung sicher befüllten Watchfolder.
