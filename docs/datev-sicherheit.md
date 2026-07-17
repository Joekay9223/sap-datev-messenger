# DATEV-Belegtransfer: Sicherheitskonzept

## Zulässige Übergaben

1. Eine originale PDF-Datei, **oder**
2. ein geschlossenes Buchungsdaten-ZIP mit exakt drei Einträgen:
   - `document.xml`
   - `Eingangsrechnung-<SAP-DocNum>.xml`
   - `Eingangsrechnung-<SAP-DocNum>.pdf`

Alle drei Namen müssen dieselbe SAP-Dokumentnummer verwenden. Zusätzliche, fehlende oder lose Metadaten machen eine Übergabe ungültig.

## Pflichtprüfungen vor einem Transfer

- Zielpfad ist als DATEV-Watchfolder registriert.
- Datei ist entweder PDF oder geprüftes ZIP.
- Bei ZIP: Eintragsliste, Namensbezug und lokale DATEV-XSDs sind gültig.
- Rechnungsdaten stammen aus SAP; insbesondere wird der DATEV-BU-Code aus `AVT1.DatevCode` abgeleitet.
- Audit-Protokoll enthält Prüfergebnis, Paket-Identität und Transfer-Evidenz.

## Referenzfall

Für SAP-Steuerkennzeichen `V2 / 19 % Vorsteuer` wurde `AVT1.DatevCode = 9` als DATEV-BU-Code bestätigt. Die Implementierung übernimmt grundsätzlich den von SAP gelieferten Code und rät ihn nicht aus historischen Paketen.

## Nicht zulässig

- Lose `document.xml`, Rechnungs-XML, CSV, TXT, README oder sonstige Metadaten im Watchfolder.
- Übernahme eines BU-Codes aus Altarchiven ohne SAP-Abgleich.
- Bewertung eines SAP-Anhangs als Versandnachweis.
