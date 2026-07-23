@echo off
setlocal
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"
set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
"%MSBUILD%" "%REPO%\src\WhoseIptv.Package\WhoseIptv.Package.wapproj" /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=true
echo.
echo Expected Store files on Desktop after the build:
echo   WhoseIptv.Package_1.0.11.0_x64_bundle.msixupload
echo   WhoseIptv.Package_1.0.11.0_x64.msixbundle
pause
