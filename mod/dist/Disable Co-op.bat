@echo off
rem Turns Reka Co-op off: Reka starts exactly as it does without the mod. The mod loader (BepInEx) is switched off
rem with it, so other BepInEx mods are off too until "Enable Co-op". Your saves are not touched.
cd /d "%~dp0"
set "SYS=%SystemRoot%\System32"
if not exist "Reka.exe" (
    echo Run this from Reka's game folder ^(the one with Reka.exe^).
    goto end
)
if not exist "winhttp.dll" if exist "winhttp.dll.off" (
    echo Reka Co-op is already OFF. Run "Enable Co-op" to turn it back on.
    goto end
)
if not exist "winhttp.dll" (
    echo Reka Co-op isn't installed in this folder: %cd%
    goto end
)

set tries=0
:wait
"%SYS%\tasklist.exe" /fi "imagename eq Reka.exe" /fo csv /nh 2>nul | "%SYS%\find.exe" /i "Reka.exe" >nul
if errorlevel 1 goto closed
set /a tries+=1
if %tries% gtr 20 goto running
if %tries%==1 echo Reka is still running. Waiting for it to close...
"%SYS%\ping.exe" -n 2 127.0.0.1 >nul
goto wait

:closed
if exist "winhttp.dll.off" del /q "winhttp.dll.off"
ren "winhttp.dll" "winhttp.dll.off"
if exist "winhttp.dll" goto failed
echo.
echo   Reka Co-op is OFF.
echo   Reka now starts as the normal game. Run "Enable Co-op" to turn it back on.
echo.
goto end

:running
echo.
echo   NOT CHANGED: Reka is still running.
echo   Close Reka, wait a few seconds, then run this again.
echo.
goto end

:failed
echo.
echo   NOT CHANGED: Windows would not let this script rename winhttp.dll in
echo   %cd%
echo   Right-click "Disable Co-op" and choose "Run as administrator", or rename
echo   winhttp.dll to winhttp.dll.off yourself.
echo.

:end
pause
