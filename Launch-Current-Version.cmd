@echo off
setlocal

:: Priority 1: Launch latest packaged Release build with newest features
set "APP=%~dp0src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App\IptvPlayer.App.exe"
if exist "%APP%" (
  echo Launching latest packaged Release build: "%APP%"
  start "Whose IPTV - Release" /D "%~dp0src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App" "%APP%"
  exit /b 0
)

:: Priority 2: Launch standalone Release build
set "APP_STANDALONE=%~dp0src\IptvPlayer.App\bin\x64\Release\net8.0-windows10.0.19041.0\IptvPlayer.App.exe"
if exist "%APP_STANDALONE%" (
  echo Launching latest standalone Release build: "%APP_STANDALONE%"
  start "Whose IPTV - Release" /D "%~dp0src\IptvPlayer.App\bin\x64\Release\net8.0-windows10.0.19041.0" "%APP_STANDALONE%"
  exit /b 0
)

:: Priority 3: Fall back to official installed Windows Store release package
powershell -NoProfile -Command "Get-AppxPackage WHOSEIPTV.WhoseIPTV" >nul 2>&1
if %ERRORLEVEL% EQU 0 (
  echo Launching official installed Store package: WHOSEIPTV.WhoseIPTV
  start "" "shell:AppsFolder\WHOSEIPTV.WhoseIPTV_jhywfcyt2h7f8!App"
  exit /b 0
)

echo No release build found. Please run Build-Store-Package.cmd.
exit /b 1
