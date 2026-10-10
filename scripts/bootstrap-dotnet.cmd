@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0bootstrap-dotnet.ps1" %*
exit /b %ERRORLEVEL%
