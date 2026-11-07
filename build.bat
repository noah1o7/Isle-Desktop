@echo off
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo  Das .NET 8 SDK fehlt. Installieren mit:
  echo     winget install Microsoft.DotNet.SDK.8
  echo  Danach dieses Fenster schliessen und build.bat erneut starten.
  echo.
  pause
  exit /b 1
)

rem Laufende Isle erst sauber beenden (stellt die Windows-Taskleiste wieder her),
rem nur falls das nicht klappt hart beenden - sonst ist dist\Isle.exe gesperrt
if exist "dist\Isle.exe" (
  "dist\Isle.exe" --quit
  timeout /t 2 /nobreak >nul
)
taskkill /im Isle.exe /f >nul 2>nul
timeout /t 1 /nobreak >nul

echo  Baue Isle ...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
  echo.
  echo  Build fehlgeschlagen - schick mir die Ausgabe oben.
  pause
  exit /b 1
)

echo.
echo  Fertig: dist\Isle.exe
start "" "dist\Isle.exe"
