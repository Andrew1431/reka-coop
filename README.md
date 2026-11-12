# Reka Co-op

Play [Reka](https://store.steampowered.com/app/1737870/) together with friends. One player hosts, and their save is the world. Friends join over Steam and walk, gather, talk and build in it together.

**Experimental alpha.** Tested with 2 players through the prologue (up to getting your chicken-legged hut). Expect bugs. Your saves are backed up automatically every time the game starts (see below).

- **Download:** see [Releases](../../releases) (also on Nexus Mods).
- **Discord:** https://discord.gg/EeuAr9yZYY (bug reports, ideas, finding people to play with).
- **Install:** copy the release zip into Reka's game folder (Steam: right-click Reka > Manage > Browse local files). No launch options. Full player instructions: [mod/README-players.txt](mod/README-players.txt).
- **Turn it off / remove it:** `Disable Co-op.bat`, `Enable Co-op.bat` and `Uninstall Co-op.bat` in the game folder. None of them touch your saves.
- **Update:** the main menu says when a new version is out; close Reka and run `Update Co-op.bat`. It downloads the newest release from this repo, checks the zip's SHA-256 against the one GitHub lists, and replaces only the mod's files (BepInEx too, if a release ships a new build). Saves and settings stay.

## Your saves are backed up
The mod zips your whole save folder (`%AppData%\RekaGame\Saves`) into `%AppData%\RekaGame\RekaCoop-Backups`:
- `before-coop_<date>.zip`: your saves exactly as they were before you ever played co-op. Kept forever.
- `<date>.zip`: every time Reka starts, before the game opens any save. The newest 10 are kept.
- `playing_<date>.zip`: every 20 minutes while you play in your own world (alone or hosting). The newest 12 are kept.
- The backups live next to your saves, not in the game folder, so uninstalling the mod or verifying the game files in Steam leaves them alone.
- To restore one: close Reka, run `Open Save Backups.bat` in the game folder, rename `Saves` to `Saves-old`, make a new empty `Saves` folder and copy the contents of a backup zip into it.

On top of that, guests never save: a joining player's game keeps the host's world in a separate folder in the game directory (`CoopSaves`) and never writes to their own saves. Only the host's game saves the shared world, into the host's own save.

## How it plays
- The host loads a save; once they're in their world, friends join from Steam ("Join Game" or an invite). Going back to the main menu ends the session.
- The story runs on the host's game. Whoever starts a talk leads it; everyone sees the same lines, choices, popups and weather.
- One shared backpack. Guests never save; their own saves are never touched.

## Bug reports
Press F8 in the game and type what went wrong. The mod saves a scrubbed report (no names, Steam ids, user or PC names, IPs or saves) on the PC of everyone in the session, in `BepInEx\RekaCoop-reports\outbox`. Nothing is sent by itself in this version: post the file on the Discord.

## Building
- Windows, .NET SDK, Reka installed, and BepInEx 6 IL2CPP (bleeding edge build 791) in a copy of the game.
- `mod/build.ps1` builds `RekaCoop.dll` against the copy's interop assemblies and installs it; `mod/package.ps1` makes the player zip; `mod/test.ps1` runs the offline network tests.
- `server/log-reporter/` is the Cloudflare Worker that receives bug reports.

## Made with AI, open for audit
All of the code in this repo, the docs included, was written by an AI (Claude, by Anthropic), directed, playtested and reviewed by the author. Commits made this way carry a `Co-Authored-By: Claude` line.

Everything that ships is here: the plugin source, the build and packaging scripts, the install/update/uninstall scripts and the bug-report server. You can read what the mod does, what it sends (`mod/RekaCoop/Diagnostics/`) and what it asks GitHub (`mod/RekaCoop/UpdateCheck.cs`), and build the DLL yourself with `mod/build.ps1` instead of using a release. Each release lists its zip's SHA-256.

## Not affiliated
A fan-made mod, not made or supported by Reka's developers. Please send co-op bugs here, not to them.

## License
[MIT No Attribution](LICENSE): do whatever you like with it. Release zips also contain BepInEx, which has its own license (LGPL-2.1).
