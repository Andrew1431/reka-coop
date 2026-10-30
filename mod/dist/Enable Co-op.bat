@echo off
rem Turns Reka Co-op back on after "Disable Co-op".
cd /d "%~dp0"
if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" (
    ren "BepInEx\plugins\RekaCoop\RekaCoop.dll.off" "RekaCoop.dll"
    echo Reka Co-op is ON.
) else if exist "BepInEx\plugins\RekaCoop\RekaCoop.dll" (
    echo Reka Co-op is already on.
) else (
    echo Reka Co-op isn't installed in this folder: %cd%
)
pause
