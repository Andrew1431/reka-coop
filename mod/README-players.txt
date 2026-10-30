REKA CO-OP (alpha)
===========


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

Discord (bugs, ideas, people to play with): https://discord.gg/EeuAr9yZYY
