# NovaNein-Datensicherung

Der zentrale Dienst kann eine tägliche, konsistente SQLite-Sicherung einschließlich der unveränderlichen PDF-Ablage erstellen. Die Funktion bleibt aus, bis ein absoluter Sicherungsordner gesetzt ist.

## Freigabe der Sicherung

In der lokalen, nicht versionierten Dienstkonfiguration setzen:

```json
"Backup": {
  "Directory": "D:\\NovaNein-Backups",
  "RetentionDays": 30
}
```

Für den dauerhaften Betrieb muss der Sicherungsordner auf einem getrennten, überwachten Laufwerk oder einer gesicherten Netzwerkfreigabe liegen. Eine Sicherung enthält pro Zeitstempel:

- `novanein.db` – konsistente SQLite-Onlinekopie,
- `documents` – die zugehörige PDF-Ablage.

Der Dienst erstellt die erste Sicherung beim Start und danach täglich. Alte Sicherungen werden gemäß `RetentionDays` entfernt.

## Wiederherstellungsregel

Eine Wiederherstellung erfolgt nur kontrolliert: Dienst stoppen, aktuellen Datenordner sichern, eine vollständige Sicherung in einen leeren Arbeitsbereich kopieren, Integrität und Stichproben prüfen und erst danach den Dienst wieder starten.

Für die Abnahme sollte ein synthetischer Testbestand verwendet werden. Dabei sind SQLite `PRAGMA integrity_check`, PDF-Hashes, Audit-Ereignisse und ein vollständiger Start des Dienstes zu prüfen. Ein lokales Anwendungsbackup ersetzt kein unabhängiges Off-host-Backup.
