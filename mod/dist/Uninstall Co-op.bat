@echo off
rem Removes Reka Co-op and the BepInEx loader it came with from the game folder. Your own Reka saves are elsewhere
rem (%AppData%\RekaGame\Saves) and are not touched. Guests' copies of friends' worlds (CoopSaves) are removed.
cd /d "%~dp0"
if not exist "Reka.exe" (
    echo Run this from Reka's game folder ^(the one with Reka.exe^).
    pause
    exit /b 1
)
echo This removes Reka Co-op and BepInEx from:
echo   %cd%
echo If you use other BepInEx mods for Reka, they will stop working too.
choice /m "Uninstall"
if errorlevel 2 exit /b 0
rmdir /s /q "BepInEx" 2>nul
rmdir /s /q "dotnet" 2>nul
rmdir /s /q "CoopSaves" 2>nul
del /q "winhttp.dll" "doorstop_config.ini" ".doorstop_version" "README-RekaCoop.txt" "Enable Co-op.bat" "Disable Co-op.bat" "Open Save Backups.bat" 2>nul
echo Done. Reka is back to the normal game.
pause
(goto) 2>nul & del "%~f0"
