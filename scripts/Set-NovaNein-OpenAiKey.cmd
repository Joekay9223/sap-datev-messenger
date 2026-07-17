@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-NovaNein-OpenAiKey.ps1"
if errorlevel 1 echo OpenAI-Schluessel konnte nicht gespeichert werden. Bitte die Fehlermeldung oben beachten.
pause
