# Dienstinstallation

1. Release erzeugen: `dotnet publish src/NovaNein.Server -c Release -r win-x64 --self-contained false -o <PublishPath>`.
2. Ein CA-signiertes Serverzertifikat in `Cert:\LocalMachine\My` mit dem konfigurierten Servernamen oder einer Test-NET-Adresse erstellen.
3. Client-Zertifikate, SAP-/OpenAI-Secrets und produktive Watchfolder außerhalb des Repositorys hinterlegen.
4. Skript prüfen und anschließend bewusst installieren.

```powershell
.\scripts\install-novanein-service.ps1 -PublishPath '<PublishPath>' -ServerCertificateThumbprint '<Thumbprint>' -Install
```

Der Dienst sollte ausschließlich über explizit konfigurierte lokale Netzbereiche erreichbar sein. Die Beispielkonfiguration nutzt dafür `192.0.2.0/24`; diese TEST-NET-Adresse ist nicht für einen echten Produktivbetrieb gedacht.

## SAP-Lesepfad mit minimalen Rechten

Der SQL-Lesepfad darf nur die erforderlichen Leserechte erhalten. Der direkte Service Layer bleibt der einzige vorgesehene SAP-Schreibpfad und wird durch die Read-only-Konfiguration nicht freigeschaltet.

```powershell
.\scripts\Grant-NovaNein-SapReadOnlySql.ps1 `
  -ServiceName 'NovaNein' `
  -ServerInstance 'localhost' `
  -Database '<SAP-Firmendatenbank>' `
  -Apply
```

SAP- und OpenAI-Zugangsdaten werden ausschließlich über lokale Secret-Setter oder den Windows Credential Store hinterlegt. Sie gehören weder in JSON-Dateien noch in Git.
