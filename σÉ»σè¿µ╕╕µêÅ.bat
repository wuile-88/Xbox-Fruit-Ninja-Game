@echo off
setlocal
cd /d "%~dp0"

rem ============================================================
rem   Fruit Slash - Kinect Motion Game  /  Launcher
rem
rem   1) start the Kinect bridge
rem   2) open the game FULLSCREEN (kiosk mode)
rem
rem   No web server needed. No Python needed.
rem
rem   Autostart on boot:  Win+R  ->  shell:startup
rem                       put a shortcut of this file there
rem ============================================================

set PORT=8181

rem ---------- Bridge options ----------
rem   --cutout   show only the player, remove the background
rem   --width    output width; lower to 640 on weak machines
rem   --fps      video fps (skeleton always runs at 30fps)
set BRIDGE_ARGS=--port %PORT% --width 960 --fps 20 --quality 70 --cutout --bg 0B1F26

echo ============================================
echo   Fruit Slash - starting
echo ============================================
echo.

rem ---------- 1. Kinect bridge ----------
echo [1/2] Starting Kinect bridge...
if not exist "KinectBridge\KinectBridge.exe" (
  echo.
  echo [ERROR] KinectBridge.exe not built yet.
  echo         Open the KinectBridge folder and run build.bat first.
  echo.
  pause
  exit /b 1
)
taskkill /f /im KinectBridge.exe >nul 2>&1
start "KinectBridge" /min "KinectBridge\KinectBridge.exe" %BRIDGE_ARGS%

rem Kinect cold start takes a few seconds
ping -n 7 127.0.0.1 >nul

rem ---------- 2. Browser, fullscreen ----------
echo [2/2] Opening the game fullscreen...

set "GAME=%CD%\index.html"
set "PROFILE=%LOCALAPPDATA%\FruitSlashKiosk"

set "CHROME="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" set "CHROME=C:\Program Files\Google\Chrome\Application\chrome.exe"
if not defined CHROME if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" set "CHROME=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
if not defined CHROME if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" set "CHROME=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"

set "EDGE="
if exist "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" set "EDGE=C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if not defined EDGE if exist "C:\Program Files\Microsoft\Edge\Application\msedge.exe" set "EDGE=C:\Program Files\Microsoft\Edge\Application\msedge.exe"

if defined CHROME (
  echo       Using Chrome.
  taskkill /f /im chrome.exe /fi "WINDOWTITLE eq Fruit*" >nul 2>&1
  start "" "%CHROME%" ^
    --kiosk --start-fullscreen ^
    --user-data-dir="%PROFILE%" ^
    --allow-file-access-from-files ^
    --autoplay-policy=no-user-gesture-required ^
    --no-first-run --no-default-browser-check ^
    --disable-session-crashed-bubble --disable-infobars ^
    --overscroll-history-navigation=0 --disable-pinch ^
    "%GAME%"
  goto done
)

if defined EDGE (
  echo       Chrome not found - using Microsoft Edge.
  start "" "%EDGE%" ^
    --kiosk "%GAME%" --edge-kiosk-type=fullscreen --kiosk-idle-timeout-minutes=0 ^
    --user-data-dir="%PROFILE%" ^
    --allow-file-access-from-files ^
    --autoplay-policy=no-user-gesture-required ^
    --no-first-run --no-default-browser-check
  goto done
)

echo       Neither Chrome nor Edge found - using the default browser.
echo       Press F11 to go fullscreen.
start "" "%GAME%"

:done
echo.
echo   Game started.
echo.
echo   Exit fullscreen / quit : Alt+F4
echo   Open settings          : press Q, or double-click the top-right corner
echo.
echo   Closing this window will NOT stop the game.
ping -n 6 127.0.0.1 >nul
