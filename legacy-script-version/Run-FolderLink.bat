@echo off
rem Double-click launcher for FolderLink.ps1.
rem
rem Files downloaded from a browser are tagged by Windows as coming from
rem the internet ("Mark of the Web"). Unblock-File clears that tag for our
rem own script specifically, instead of changing any system-wide security
rem setting. The script then elevates itself to Administrator on its own
rem (a UAC prompt will appear), so this .bat does not request elevation.
powershell.exe -NoProfile -Command "Unblock-File -LiteralPath '%~dp0FolderLink.ps1'" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FolderLink.ps1"
