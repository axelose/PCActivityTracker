@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0self-test.ps1" %*
exit /b %ERRORLEVEL%
