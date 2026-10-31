@echo off
rem Turns Reka Co-op off: Reka starts as the normal single-player game. Other BepInEx mods keep working.
cd /d "%~dp0"
if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll" (
    tasklist /fi "imagename eq Reka.exe" | find /i "Reka.exe" >nul && (
        echo Reka is running. Close it first, then run this again.
        pause
        exit /b 1
    )
    ren "BepInEx\plugins\RekaCoop\RekaCoop.dll" "RekaCoop.dll.off"
    if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" (echo Reka Co-op is OFF. Run "Enable Co-op" to turn it back on.) else (echo Couldn't switch it. Close Reka and try again.)
) else if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" (
    echo Reka Co-op is already off.
) else (
    echo Reka Co-op isn't installed in this folder: %cd%
)
pause
