@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-msix.ps1" %*
exit /b %ERRORLEVEL%
