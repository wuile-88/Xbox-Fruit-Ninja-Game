@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Building KinectBridge.exe
echo ============================================
echo.

rem ---- Stop any running instance, otherwise the exe file is locked ----
echo [1/4] Closing running instances...
taskkill /f /im KinectBridge.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
if exist "KinectBridge.exe" del /f /q "KinectBridge.exe" >nul 2>&1
if exist "KinectBridge.exe" (
  echo.
  echo [ERROR] KinectBridge.exe is locked and cannot be replaced.
  echo         Close the KinectBridge console window and the game launcher,
  echo         then run this script again.
  echo.
  pause
  exit /b 1
)

rem ---- Locate Kinect SDK ----
echo [2/4] Looking for Kinect SDK...
set "KDLL=%KINECTSDK20_DIR%Assemblies\Microsoft.Kinect.dll"
if not exist "%KDLL%" set "KDLL=C:\Program Files\Microsoft SDKs\Kinect\v2.0_1409\Assemblies\Microsoft.Kinect.dll"
if not exist "%KDLL%" (
  echo.
  echo [ERROR] Microsoft.Kinect.dll not found.
  echo         Install Kinect for Windows SDK 2.0 first:
  echo         https://www.microsoft.com/download/details.aspx?id=44561
  echo.
  pause
  exit /b 1
)
echo       Found: %KDLL%

rem ---- Locate C# compiler (ships with Windows, no Visual Studio needed) ----
echo [3/4] Looking for C# compiler...
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo.
  echo [ERROR] csc.exe not found. Install .NET Framework 4.x
  echo.
  pause
  exit /b 1
)
echo       Found: %CSC%

echo [4/4] Compiling...
"%CSC%" /nologo /platform:x64 /target:exe /optimize+ /out:KinectBridge.exe ^
  /r:"%KDLL%" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Net.Http.dll ^
  Program.cs

if errorlevel 1 (
  echo.
  echo [FAILED] Compile error. Please send a screenshot of the messages above.
  echo.
  pause
  exit /b 1
)

if not exist "KinectBridge.exe" (
  echo.
  echo [FAILED] No output file produced.
  echo.
  pause
  exit /b 1
)

rem Kinect SDK normally registers in the GAC; this copy is a fallback
copy /y "%KDLL%" . >nul 2>&1

echo.
echo ============================================
echo   SUCCESS - KinectBridge.exe is ready
echo ============================================
echo.
echo   Next: go up one folder and run START-GAME.bat
echo.
pause
