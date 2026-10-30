@echo off
rem Turns Reka Co-op off: Reka starts as the normal single-player game. Other BepInEx mods keep working.
cd /d "%~dp0"
if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll" (
    ren "BepInEx\plugins\RekaCoop\RekaCoop.dll" "RekaCoop.dll.off"
    echo Reka Co-op is OFF. Run "Enable Co-op" to turn it back on.
) else if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" (
    echo Reka Co-op is already off.
) else (
    echo Reka Co-op isn't installed in this folder: %cd%
)
pause
