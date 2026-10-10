@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-public-source.ps1" %*
exit /b %ERRORLEVEL%
