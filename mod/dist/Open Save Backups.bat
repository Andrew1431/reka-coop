@echo off
rem Opens the folder with the automatic backups of your Reka saves, and the save folder next to it.
set "B=%AppData%\RekaGame\RekaCoop-Backups"
if not exist "%B%" (
    echo No backups yet: they are made when Reka starts with the co-op mod, once you have a save.
    pause
    exit /b 0
)
echo Your save backups are in: %B%
echo.
echo To restore one:
echo   1. Close Reka.
echo   2. Rename the folder %AppData%\RekaGame\Saves to Saves-old.
echo   3. Make a new, empty folder called Saves there.
echo   4. Open a backup zip and copy everything in it into the new Saves folder.
echo.
echo before-coop_... is your saves from before you ever played co-op.
echo playing_...     were made every 20 minutes while you played.
echo The others were made each time Reka started.
start "" explorer "%B%"
pause
