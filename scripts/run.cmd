@echo off
setlocal
set "APP_EXE="
set "RUNTIME=win-x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "RUNTIME=win-arm64"

for /f "delims=" %%D in ('dir /b /ad /o-n "%~dp0..\artifacts\release\PCActivityTracker-*-%RUNTIME%" 2^>nul') do (
  if not defined APP_EXE if exist "%~dp0..\artifacts\release\%%D\PCActivityTracker.exe" set "APP_EXE=%~dp0..\artifacts\release\%%D\PCActivityTracker.exe"
)

if defined APP_EXE goto launch

set "APP_EXE=%~dp0..\src\PCActivityTracker\bin\Release\net10.0-windows10.0.22000.0\PCActivityTracker.exe"
call "%~dp0build.cmd" Release
if errorlevel 1 exit /b %ERRORLEVEL%

:launch
if exist "%~dp0..\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%~dp0..\.dotnet"
  set "DOTNET_ROOT_X64=%~dp0..\.dotnet"
  set "DOTNET_MULTILEVEL_LOOKUP=0"
)
start "" "%APP_EXE%"
exit /b 0
