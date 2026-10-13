@echo off
setlocal
title Build Dynamic Island
cd /d "%~dp0"

echo.
echo  ============================================
echo    Building Dynamic Island
echo  ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk
dotnet --list-sdks | findstr /b "8. 9. 10." >nul
if errorlevel 1 goto :nosdk

tasklist /fi "imagename eq DynamicIsland.exe" | find /i "DynamicIsland.exe" >nul
if not errorlevel 1 (
    echo  Dynamic Island is currently running.
    echo  Right-click the island, choose "Exit Dynamic Island", then run this again.
    echo.
    pause
    exit /b 1
)

echo  This takes a minute or two the first time...
echo.
if not exist "src\Assets\tabler-icons.ttf" (
    echo  Downloading the line icon set ^(one time^)...
    powershell -NoProfile -ExecutionPolicy Bypass -File "tools\get-icons.ps1"
    echo.
)
dotnet publish "src\DynamicIsland.csproj" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o "App" -nologo
if errorlevel 1 (
    echo.
    echo  The build failed. Copy the red error text above and send it to Claude.
    echo.
    pause
    exit /b 1
)

echo.
echo  Done! Your app is at:  %~dp0App\DynamicIsland.exe
echo.
choice /m " Start Dynamic Island now"
if errorlevel 2 exit /b 0
start "" "App\DynamicIsland.exe"
exit /b 0

:nosdk
echo  You need Microsoft's free .NET 8 SDK to build this (one-time install).
echo.
echo  Opening the download page now. Choose the "SDK 8.0" Windows x64 installer,
echo  install it, then double-click Build.cmd again.
echo.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1
