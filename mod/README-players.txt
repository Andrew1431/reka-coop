REKA CO-OP (alpha)
==================

Play Reka with friends. One player hosts: their world is the one everyone plays in. Friends join over Steam.
No launch options, ports or IP addresses.


INSTALL (everyone)
------------------
1. Your saves are backed up automatically (see YOUR SAVES ARE SAFE below). An extra copy of
   %AppData%\RekaGame\Saves never hurts.
2. In Steam: right-click Reka > Manage > Browse local files. That opens the game folder (it has Reka.exe).
3. Copy everything from this zip into that folder.
4. Start Reka and wait at the main menu. The first start takes a few minutes while BepInEx sets up; later starts
   are normal speed.


YOUR SAVES ARE SAFE
-------------------
The mod zips your whole save folder (%AppData%\RekaGame\Saves) into %AppData%\RekaGame\RekaCoop-Backups:
- before-coop_<date>.zip  your saves as they were before you ever played co-op. Kept forever.
- <date>.zip              every time Reka starts, before the game opens any save. The newest 10 are kept.
- playing_<date>.zip      every 20 minutes while you play in your own world (alone or hosting). The newest 12
                          are kept, so about the last four hours of play.
They live next to your saves, not in the game folder, so uninstalling or verifying the game keeps them.

To restore a backup:
1. Close Reka.
2. Run "Open Save Backups.bat" in the game folder (or open %AppData%\RekaGame\RekaCoop-Backups yourself).
3. Rename the folder %AppData%\RekaGame\Saves to Saves-old.
4. Make a new, empty folder called Saves in its place.
5. Open the backup zip you want and copy everything in it into the new Saves folder.
6. Start Reka. If it looks right, Saves-old can go.
Guests never save: the host's world is kept in the game folder under CoopSaves, and a guest's own saves are
never written. Only the host's game saves the shared world, into the host's own save.


PLAY
----
Host: load your save (or start a new one). Once you're in your world, friends can join: they see "Join Game" on you
in their Steam friends list, or you invite them (Shift+Tab > right-click a friend > Invite to Game, or the
"Invite friends" button at the top left).

Guest: be at Reka's main menu (or have Reka closed), then accept the invite or click "Join Game" in Steam. The
host's world loads by itself. Your own saves are never touched: the host's world is kept in the game folder under
CoopSaves, and guests never save.

Leaving and switching:
- Stop playing with a host: pause > back to the main menu. You are back on your own saves at once.
- Rejoin: accept a new invite or click "Join Game" on the host in Steam, from the main menu.
- Host your own world after being a guest: just load your save from the main menu. Friends can then join you.
- Join someone while you are in your own world: go back to the main menu first, then join from Steam.
- If the host goes back to the main menu (to load another save, say) or closes Reka, everyone else goes back to
  their own main menu. Join again from Steam once the host is back in a world.
- If joining doesn't work within a minute or two, the game says why and puts you back on your own menu and
  saves; ask for a new invite. Up to 4 players.

Everyone needs the same version of this mod; anyone on a different one is told so.


TURN IT OFF AND ON
------------------
- "Disable Co-op.bat" (in the game folder): Reka starts as the normal single-player game.
- "Enable Co-op.bat": turns it back on.
- "Uninstall Co-op.bat": removes the mod and BepInEx from the game folder. Your saves are not touched.
- "Open Save Backups.bat": opens the folder with your save backups and says how to restore one.


UPDATING
--------
When a new version is out, the main menu says so. Close Reka and run "Update Co-op.bat" in the game folder. It
downloads the newest release from GitHub (github.com/Andrew1431/reka-coop/releases), checks its SHA-256 checksum
and replaces the mod's files. Your saves and settings are kept, and a switched-off mod stays off. Everyone in a
session needs the same version, so update together.
Turn the menu check off with  CheckForUpdates = false  in BepInEx\config\com.andrew1431.rekacoop.cfg.


WHAT TO EXPECT
--------------
- Whoever starts a talk leads it and picks the answers; the others watch.
- Quest rewards go to everyone. World pickups go to whoever grabs them first. Everyone shares one backpack.
- When the host travels to a new map, guests follow automatically.
- The top-left corner shows the connection and ping.
- Guests look like the character in their own newest save (or a random look if they've never played).



SOMETHING WENT WRONG? PRESS F8
------------------------------
Press F8 in the game (the top-left corner reminds you), type one line about what went wrong and press Enter.
The mod then saves a report on the PC of everyone in the session, all under the same report id.
- Nothing is sent anywhere by itself in this version. The report is a file in
  BepInEx\RekaCoop-reports\outbox in the game folder (the newest one ending in -report.json.gz).
- Post that file on the Discord below with a line about what happened; ask the others in your session to post
  theirs too.
- In the file: the mod's own lines from BepInEx\LogOutput.log (plus other warnings and errors), this session's
  lines from desync.log, the mod, game and protocol versions, and session details (host or guest, number of
  players, ping, scene, frame rate).
- Taken out of it: player and Steam names (they become "player 1", ...), Steam ids, your Windows user name,
  PC name and IP addresses. Saves are never in it.
A report is also written there when you quit and after a crash. The full log is BepInEx\LogOutput.log.


MADE WITH AI, OPEN FOR AUDIT
----------------------------
All of this mod's code was written by an AI (Claude, by Anthropic), directed, playtested and reviewed by the
mod's author. The full source code, build scripts and the bug-report server are public at
    https://github.com/Andrew1431/reka-coop
so anyone can read exactly what the mod does, what it sends and where, and build the DLL themselves.
Each release lists the SHA-256 of its zip. The mod is a fan project, not made or supported by Reka's developers.

Discord (bugs, ideas, people to play with): https://discord.gg/EeuAr9yZYY
