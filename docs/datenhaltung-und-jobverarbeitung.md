# Datenhaltung und Jobverarbeitung

## Entscheidung

Für die interne Eigenlösung wird zunächst **SQLite für Metadaten und Prozesszustände** verwendet. PDFs und erzeugte DATEV-Pakete liegen als normale Dateien in einem internen, nur vom Backend verwalteten Dateispeicher.

Ein eigener Microsoft SQL Server oder PostgreSQL ist für die erwartete Datenmenge und den vorgesehenen Einzelservicebetrieb nicht erforderlich.

## Warum SQLite hier passt

Der Aufbau ist trotz mehrerer SAP-Arbeitsplätze logisch ein Einzelserver-System:

- alle schwebenden SAP-Fenster kommunizieren per interner API mit einem Backend;
- nur das Backend öffnet die Datenbank;
- ein Hintergrundworker verarbeitet Validierungs- und DATEV-Aufträge;
- die Clients greifen niemals direkt auf die SQLite-Datei zu.

Damit entstehen keine verteilten Datenbankzugriffe. SQLite liefert Transaktionen, Fremdschlüssel, Indizes und zuverlässige Wiederanläufe ohne separaten Datenbankserver, Benutzerverwaltung oder Lizenz-/Wartungsaufwand.

## Größenabschätzung

Unter der konservativen Annahme, dass die genannten Mengen monatlich anfallen:

- 200 Eingangsbelege × 2 MB × 12 = ungefähr 4,8 GB pro Jahr;
- 100 sehr kleine Ausgangsrechnungen erzeugen nur einen vergleichsweise kleinen Zusatz;
- über zehn Jahre liegt der reine Dokumentbestand ungefähr in der Größenordnung von 50 GB.

Die SQLite-Datenbank enthält nicht die PDF-Binärdaten, sondern Metadaten, Status, Extraktionen, Vergleiche und Auditinformationen. Sie bleibt deshalb auch bei mehreren zehntausend Rechnungen voraussichtlich deutlich kleiner als der Dokumentbestand.

## Speicheraufteilung

```text
data/
  nova-nein.sqlite
  originals/
    ab/cd/<sha256>.pdf
  derived/
    previews/
  packages/
    2026/07/<package-id>/
  backups/
  quarantine/
  work/
```

- `originals`: unveränderte eingegangene bzw. erzeugte PDFs, adressiert über SHA-256;
- `derived`: optionale Vorschauen, niemals Ersatz für das Original; lokale OCR-Ableitungen werden nicht erzeugt;
- `packages`: vollständig geprüfte DATEV-Pakete und Validierungsreports;
- `quarantine`: falsche, unlesbare oder widersprüchliche Dokumente;
- `work`: temporäre Erstellung; niemals als DATEV-Watchfolder verwenden;
- Watchfolder: enthält nur das abschließend validierte ZIP während der Übergabe an DATEV Belegtransfer.

Die Dateien können auf demselben internen Windows-Server liegen. Die SQLite-Datei muss auf einem lokalen Datenträger liegen und darf nicht von mehreren Rechnern über SMB geöffnet werden.

## Datenbankeinstellungen

- `PRAGMA foreign_keys = ON`;
- WAL-Modus auf lokalem Datenträger;
- definierter `busy_timeout`;
- kontrollierte Checkpoints;
- Migrationen mit fortlaufender Schema-Version;
- tägliches Online-Backup der SQLite-Datei;
- separates Backup von Originalen und Paketen;
- regelmäßiger automatisierter Restore-Test.

SQLite-WAL ist nicht für den Zugriff mehrerer Rechner auf eine Datenbankdatei über ein Netzwerkdateisystem vorgesehen. Deshalb laufen sämtliche Datenbankzugriffe im zentralen Backend.

## Ablauf nach „Datei archivieren“

Der heutige Novaline-Ablauf bleibt aus Benutzersicht erhalten, kann aber schneller und transparenter werden:

1. SAP-UI übergibt Objektart, `DocEntry` und PDF an den internen Dienst.
2. Dienst speichert Original, SHA-256 und SAP-Bezug.
3. In derselben Datenbanktransaktion entstehen Dokumentdatensatz und Validierungsauftrag.
4. Der Hintergrundworker nimmt den Auftrag innerhalb weniger Sekunden auf.
5. Dokumentextraktion und SAP-Vergleich laufen.
6. Bei Grün wird das DATEV-Paket erzeugt und validiert.
7. Das fertige ZIP wird zunächst außerhalb des Watchfolders geschlossen und erneut gelesen.
8. Erst dann erfolgt ein atomarer Dateiverschub in den DATEV-Watchfolder.
9. Worker verfolgt Abholung, Archivmatch, `UploadSucceeded` und `JobFinalized`.
10. Das schwebende UI aktualisiert den Status über Polling oder Server-Sent Events.

Eine feste Wartezeit von 60 Sekunden ist nicht notwendig. Die Verzögerung ergibt sich nur noch aus Verarbeitung und DATEV Belegtransfer.

## Jobzustände

```text
ATTACHED
→ QUEUED_FOR_VALIDATION
→ VALIDATING
→ VALIDATED | MANUAL_REVIEW | MISMATCH_BLOCKED
→ PACKAGE_BUILDING
→ PACKAGE_VALIDATED
→ QUEUED_TO_BTT
→ BTT_PICKED_UP
→ UPLOAD_SUCCEEDED
→ JOB_FINALIZED
```

Jeder Zustandswechsel wird transaktional gespeichert. Nach einem Neustart setzt der Worker nicht abgeschlossene Aufträge idempotent fort.

## Tabellen auf hoher Ebene

- `documents`
- `document_files`
- `sap_document_snapshots`
- `extraction_runs`
- `extracted_fields`
- `validation_runs`
- `validation_findings`
- `manual_reviews`
- `datev_packages`
- `transfer_attempts`
- `jobs`
- `audit_events`

PDF und ZIP werden über Hash, Dateigröße und internen Pfad referenziert.

## Wann PostgreSQL sinnvoll würde

Ein Wechsel zu PostgreSQL ist erst nötig, wenn mindestens eines dieser Merkmale eintritt:

- mehrere unabhängige Backend-Instanzen schreiben gleichzeitig;
- Hochverfügbarkeit mit automatischem Failover wird verlangt;
- externe Systeme benötigen direkten SQL-Zugriff;
- sehr viele parallele Verarbeitungsschritte und dauerhafte Schreiblast;
- mehrere Firmen/Mandanten sollen als Plattform betrieben werden.

Das Datenmodell und die Anwendungsschnittstellen sollen datenbankneutral gehalten werden, damit eine spätere Migration möglich bleibt.

## Lokale Aufbewahrung

Die lokale Ablage ist nicht das steuerrechtlich maßgebliche Archiv; dies bleibt DATEV. Dennoch werden Original und Transferpaket mindestens bis zur bestätigten Finalisierung und erfolgreichen Datensicherung aufbewahrt. Aufgrund des geringen Volumens ist eine dauerhafte interne Aufbewahrung technisch einfacher und günstiger als komplexe frühe Löschregeln.

## Quellen

- [SQLite: Appropriate Uses](https://www.sqlite.org/whentouse.html)
- [SQLite: Database File Format und WAL](https://www.sqlite.org/fileformat.html)
- [SQLite: Online Backup API](https://www.sqlite.org/backup.html)
