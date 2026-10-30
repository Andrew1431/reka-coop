@echo off
rem Updates Reka Co-op to the newest release on GitHub. Close Reka first. Your saves are not touched.
cd /d "%~dp0"
if not exist "BepInEx\plugins\RekaCoop\update.ps1" (
    echo Reka Co-op isn't installed in this folder: %cd%
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "BepInEx\plugins\RekaCoop\update.ps1" -GameDir "%cd%"
pause
