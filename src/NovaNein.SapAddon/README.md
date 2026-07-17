# NovaNein SAP-Arbeitsplatz-Add-on

Dieses Projekt zielt auf .NET Framework 4.8 und ist Bestandteil von `NovaNein.sln`. Es enthält den mTLS-Client, die Dokumentkontrakte, die Ampelanzeige, den begründeten Review-Dialog für gelbe und rote Fachprüfungen sowie die Reminder- und PDF-Scan-Oberfläche. Der ausführbare SAP-UI-Host liegt im getrennten Projekt `NovaNein.SapAddonHost`.

`NovaNeinServerClient` liest das individuelle Arbeitsplatzzertifikat aus `Cert:\LocalMachine\My`. Für Eingangsrechnungen überträgt er die PDF mit `DocEntry` und `DocNum` an `POST /api/v1/documents/incoming`; für Ausgangsrechnungen verwendet er `POST /api/v1/documents/outgoing/{docEntry}/generate`. Der Server liest den SAP-Beleg erneut über seinen strikt schreibgeschützten SAP-Lesepfad und lehnt eine abweichende `DocNum` ab.

Vor jedem Drop wird der aktive SAP-Kontext erneut geprüft. Geschlossene, gewechselte oder nicht unterstützte Formulare deaktivieren die Übergabe. Beim erneuten Öffnen eines Belegs lädt der Client den vorhandenen NovaNein-Status über Richtung und `DocEntry`; dadurch werden gelbe und rote Prüfungen samt Gründen nach Zeitüberschreitung oder Neustart wiederhergestellt. `NeedsReview` wird gelb, `Rejected` rot und `Approved` beziehungsweise `AttachedToSap` grün dargestellt. Jede manuelle Freigabe verlangt eine schriftliche Begründung und eine zweite Bestätigung.

`CoresuitePdfExporter` kapselt die vorhandene Coresuite-API und die ausgewählte Print-Definition. Zugangsdaten werden weder gespeichert noch über die NovaNein-Server-API gesendet. Der automatische, nichtinteraktive Coresuite-Ende-zu-Ende-Export ist noch nicht betrieblich abgenommen und bleibt ein separates Freigabegate.

Das schwebende Belegfenster zeigt bei einer geöffneten Eingangs- oder Ausgangsrechnung neben dem Hinweis für Drag-and-Drop immer den Button „PDF auswählen“. Damit kann eine PDF auch über den Windows-Dateidialog ausgewählt werden; beide Wege verwenden dieselbe Kontextprüfung und denselben sicheren Upload. Die PDF-Aktion bleibt gesperrt, solange die NovaNein-Selbstprüfung keine erreichbare und kompatible Serververbindung bestätigt.

Der aktuelle klassische SAP-Kandidat hat die Produktversion 1.1.0 und die technische SAP-Registrierungsversion 1.1.0.2. Er wurde offline als x64-Paket geprüft und auf dem SAP-Server zum Import bereitgestellt. Die lokale Installation, der Hoststart, der sichtbare PDF-Button und die Selbstprüfung werden gemeinsam abgenommen.
