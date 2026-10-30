REKA CO-OP (alpha)
==================

Play Reka with friends. One player hosts: their world is the one everyone plays in. Friends join over Steam.
No launch options, ports or IP addresses.


INSTALL (everyone)
------------------
1. Back up your saves first: copy %AppData%\RekaGame\Saves somewhere safe.
2. In Steam: right-click Reka > Manage > Browse local files. That opens the game folder (it has Reka.exe).
3. Copy everything from this zip into that folder.
4. Start Reka and wait at the main menu. The first start takes a few minutes while BepInEx sets up; later starts
   are normal speed.


PLAY
----
Host: load your save (or start a new one). Once you're in your world, friends can join: they see "Join Game" on you
in their Steam friends list, or you invite them (Shift+Tab > right-click a friend > Invite to Game, or the
"Invite friends" button at the top left).

Guest: be at Reka's main menu (or have Reka closed), then accept the invite or click "Join Game" in Steam. The
host's world loads by itself. Your own saves are never touched: the host's world is kept in the game folder under
CoopSaves, and guests never save.

If the host goes back to the main menu (to load another save, say), everyone else goes back to theirs. Join again
from Steam once the host is back in a world.

Everyone needs the same version of this mod; anyone on a different one is told so.


TURN IT OFF AND ON
------------------
- "Disable Co-op.bat" (in the game folder): Reka starts as the normal single-player game.
- "Enable Co-op.bat": turns it back on.
- "Uninstall Co-op.bat": removes the mod and BepInEx from the game folder. Your saves are not touched.


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

Logs, if something goes wrong: BepInEx\LogOutput.log in the game folder.


LOG REPORTS (alpha only: on unless you turn them off)
----------------------------------------------------
To help find bugs, the mod sends a report to its developer when you quit, when the game crashes (sent on the
next start) and when you press F8 to report a problem. On the first start a notice
explains this and lets you turn it off; nothing is sent before you have seen it.
- Sent: the mod's own lines from BepInEx\LogOutput.log (plus other warnings and errors), this session's lines
  from desync.log, the mod, game and protocol versions, session details (host or guest, number of players,
  ping, scene, frame rate) and a random tester id (BepInEx\config\RekaCoop\tester-id.txt).
- Removed before sending: player and Steam names (they become "player 1", ...), Steam ids, your Windows user
  name, PC name and IP addresses. Saves are never sent.
- Where: a Cloudflare server (Workers + R2 storage) run by the mod's developer, used only to fix the mod.
- F8 opens a one-line box: type what went wrong and press Enter. Everyone in the session sends their log
  under the same report id.
- Turn it off: set  UploadLogs = false  in BepInEx\config\com.andrew1431.rekacoop.cfg (or press
  "Don't send anything" on the notice). Reports not sent yet, and the last ones sent, are in
  BepInEx\RekaCoop-reports.


MADE WITH AI, OPEN FOR AUDIT
----------------------------
All of this mod's code was written by an AI (Claude, by Anthropic), directed, playtested and reviewed by the
mod's author. The full source code, build scripts and the bug-report server are public at
    https://github.com/Andrew1431/reka-coop
so anyone can read exactly what the mod does, what it sends and where, and build the DLL themselves.
Each release lists the SHA-256 of its zip. The mod is a fan project, not made or supported by Reka's developers.

Discord (bugs, ideas, people to play with): https://discord.gg/EeuAr9yZYY
