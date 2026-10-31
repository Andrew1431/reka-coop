@echo off
rem Turns Reka Co-op back on after "Disable Co-op".
cd /d "%~dp0"
if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" (
    tasklist /fi "imagename eq Reka.exe" | find /i "Reka.exe" >nul && (
        echo Reka is running. Close it first, then run this again.
        pause
        exit /b 1
    )
    ren "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" "RekaCoop.dll"
    if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll" (echo Reka Co-op is ON.) else (echo Couldn't switch it. Close Reka and try again.)
) else if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll" (
    echo Reka Co-op is already on.
) else (
    echo Reka Co-op isn't installed in this folder: %cd%
)
pause
