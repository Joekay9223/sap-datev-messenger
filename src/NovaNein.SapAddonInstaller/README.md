# Klassischer SAP-Business-One-Installer

Dieses Projekt ist der von SAP Business One erwartete x64-Setup-Wrapper für den klassischen Add-on-Registrierungsweg. Es ist Bestandteil von `NovaNein.sln`. Beim normalen Solution-Build verwendet es standardmäßig den Release-/Debug-Ausgabeordner von `NovaNein.SapAddonHost` als Payload; die Projektabhängigkeit stellt sicher, dass Host und Add-on-Bibliothek vorher gebaut werden.

Für ein veröffentlichbares SAP-Paket bleibt der explizite Paketbuild verbindlich:

```powershell
.\scripts\New-NovaNein-SapClassicPackage.ps1 `
  -PayloadDirectory '<geprüfter Release-Payload>' `
  -AddOnRegDataGenPath '<SAP-SDK>\AddOnRegDataGen.exe' `
  -OutputDirectory '<Ausgabeordner>' `
  -Version '1.1.0.0' `
  -PreviousRegisteredVersion '1.0.0.7'
```

Die technische SAP-Version ist bewusst vierteilig. SAP entfernt beim Versionsvergleich alle Punkte: `1.1.0.0` wird als `1100` und `1.0.0.7` als `1007` bewertet. Der Paketbau bricht deshalb ab, wenn die neue technische Version nicht größer als die ausdrücklich angegebene vorher registrierte Version ist.

Der Wrapper verarbeitet ausschließlich den von SAP übergebenen Parameter `Installationspfad|AddOnInstallAPI.dll`. Er entpackt die drei Laufzeitdateien, ersetzt einen vorhandenen Stand mit Sicherung und Rollback und meldet das Ergebnis mit `EndInstallEx` beziehungsweise `EndUninstall` an SAP zurück.

Installationspfad und Protokoll liegen getrennt von Client- und Serverdaten unter:

`C:\ProgramData\NovaNein\SapAddonInstaller`

Vor einer Registrierung kann das Paket ohne SAP-Zugriff geprüft werden:

```powershell
.\scripts\Test-NovaNein-SapClassicPackage.ps1 `
  -PackageDirectory '<Ausgabeordner>' `
  -PreviousRegisteredVersion '1.0.0.7'
```

Der aktuelle Kandidat hat die Produktversion 1.1.0 und die technische SAP-Version 1.1.0.0. Er wurde offline einschließlich ARD-, Installer- und eingebetteter Host-Prüfsummen validiert und zentral erfolgreich registriert. Diese Prüfung ersetzt nicht den lokalen Installationsabschluss und den sichtbaren Starttest im SAP-Client.

Die EXE nie manuell starten. Installation und Deinstallation müssen von SAP Business One ausgelöst werden.
