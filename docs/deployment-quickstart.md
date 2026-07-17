# NovaNein Deployment-Quickstart

Dieser Stand erzeugt ein firmenneutrales Windows-Paket für eine kontrollierte interne Installation. Er enthält weder Datenbank noch Belege, Zertifikate, Zugangsdaten, DATEV-Schemas oder kundenspezifische Cockpit-Listen.

## Paketinhalt

- app — selbstständiger Windows-x64-Server
- datev-bridge — optionale lokale DATEV-Bridge
- config — neutrale Firmen- und Bridge-Vorlagen
- scripts — Installer und sichere Secret-Setter
- docs — diese Installationsanleitung
- SHA256SUMS.txt — Prüfsummen sämtlicher Paketdateien

## Voraussetzungen

- Windows 10/11 oder Windows Server x64
- Administratorrechte für Dienst, Datenverzeichnis und Firewallregel
- SAP Business One mit einem technisch schreibgeschützten SQL- oder Service-Layer-Zugang
- OpenAI API-Key für die vollständige PDF-OCR und strukturierte Rechnungsinterpretation
- Für DATEV zusätzlich die legal bezogenen DATEV-v6.0-XSDs und eine separat konfigurierte Bridge

## Installation

Eine PowerShell als Administrator öffnen und zuerst nur die Paketprüfung ausführen:

    powershell -ExecutionPolicy Bypass -File .\scripts\Install-NovaNein-Standalone.ps1 -PackageRoot .

Danach installieren und das tatsächliche lokale Netz ausdrücklich freigeben:

    powershell -ExecutionPolicy Bypass -File .\scripts\Install-NovaNein-Standalone.ps1 -PackageRoot . -Install -Start -AllowedCidrs 192.0.2.0/24

Das Cockpit ist anschließend unter http://SERVER-IP:5188 erreichbar.

## Zugangsdaten hinterlegen

Zugangsdaten niemals in appsettings-Dateien schreiben. Die mitgelieferten Setter speichern Werte maskiert im geschützten Dienstkontext:

    powershell -ExecutionPolicy Bypass -File .\scripts\Set-NovaNein-ServiceSecrets.ps1 -ServiceName NovaNein
    powershell -ExecutionPolicy Bypass -File .\scripts\Set-NovaNein-OpenAiKey.ps1 -ServiceName NovaNein

Anschließend den Dienst neu starten und /health sowie die SAP-Arbeitsliste prüfen.

NovaNein übergibt die Original-PDF direkt an die OpenAI Responses API. OpenAI verarbeitet PDF-Text und Seitenbilder gemeinsam und liefert ein strikt schemafestes Ergebnis. SAP-Sollwerte werden nicht mitgesendet; Schema-/Plausibilitätsprüfung und SAP-Abgleich laufen lokal. Ein Windows-OCR- oder Regex-Fallback ist nicht enthalten.

## DATEV bewusst separat aktivieren

Die Grundinstallation lässt automatische Paketbildung und Transfer aus. Vor der Aktivierung müssen Mandant, Konten, originale DATEV-XSDs, Bridge-Benutzer und Zielpfade firmenspezifisch geprüft werden.

In überwachte DATEV-Ordner dürfen niemals lose XML-, CSV-, TXT- oder README-Dateien gelangen. Erlaubt ist nur der bestätigte Importweg: Original-PDF oder ein vollständig XSD-validiertes, geschlossenes ZIP im vereinbarten Paketformat.

## Noch kein One-Key-Setup

Ein OpenAI-Key allein genügt nicht: SAP-Endpunkt und Leserechte, DATEV-Mandant und Konten, Netzwerkbereich sowie die DATEV-XSDs sind unternehmensabhängig und müssen ausdrücklich eingerichtet werden. Ein späterer Setup-Assistent kann diese Schritte vereinfachen, darf sie aber nicht erraten.
