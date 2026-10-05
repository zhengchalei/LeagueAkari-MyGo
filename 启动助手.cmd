@echo off
cd /d "%~dp0"
if exist "mygo\build\Timo-MyGo.exe" (
  start "" "mygo\build\Timo-MyGo.exe"
  exit /b 0
)
echo Build Timo MyGo first: npm run desktop:install then npm run mygo:build
pause
exit /b 1
