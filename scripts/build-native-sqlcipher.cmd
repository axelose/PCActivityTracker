@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-native-sqlcipher.ps1" %*
exit /b %ERRORLEVEL%
