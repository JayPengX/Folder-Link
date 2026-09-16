@echo off
rem Double-click launcher for FolderLink.ps1.
rem The script elevates itself to Administrator (a UAC prompt will appear),
rem so this .bat does not need to request elevation itself.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FolderLink.ps1"
