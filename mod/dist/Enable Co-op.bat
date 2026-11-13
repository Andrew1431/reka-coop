@echo off
rem Turns Reka Co-op back on after "Disable Co-op".
cd /d "%~dp0"
set "SYS=%SystemRoot%\System32"
if not exist "Reka.exe" (
    echo Run this from Reka's game folder ^(the one with Reka.exe^).
    goto end
)
set "old=BepInEx\plugins\RekaCoop\RekaCoop.dll"
if exist "winhttp.dll" if not exist "%old%.off" (
    echo Reka Co-op is already ON.
    goto end
)
if not exist "winhttp.dll" if not exist "winhttp.dll.off" (
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
if not exist "winhttp.dll" ren "winhttp.dll.off" "winhttp.dll"
rem Switched off by an older version of these scripts (0.4.1 and before renamed the mod's own file).
if exist "%old%.off" if not exist "%old%" ren "%old%.off" "RekaCoop.dll"
if exist "%old%.off" if exist "%old%" del /q "%old%.off"
if not exist "winhttp.dll" goto failed
if not exist "%old%" goto failed
echo.
echo   Reka Co-op is ON.
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
echo   NOT CHANGED: Windows would not let this script rename files in
echo   %cd%
echo   Right-click "Enable Co-op" and choose "Run as administrator".
echo.

:end
pause
