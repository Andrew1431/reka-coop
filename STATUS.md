# Where we are (2026-10-29)

Protocol 46, plugin 0.3.13. Package: local/release/RekaCoop-0.3.13.zip (built for game build 26298851, hotfix 1.0.25; not tried on a second PC). Host and guest must run the same build.
The two-PC Steam test on Olga worked: invite, join, the world arriving in the guest's sandbox, names, looks, clouds, crash and rejoin, quitting to the menu, leaving the lobby, carrying hares.
Two-PC tutorial try (2026-10-27 19:15, game build 26225334, the day-one patch): the host made its lobby and the intro gate worked, but Olga's mod never reported Steam up: her log stops at the game's own Steam init ("API loaded no"), cause unknown (both accounts own the game, and the same setup worked the night before; not Family Sharing). Her log may just have been cut off when she quit. Mod removed from Olga again (`\Olga\Reka\_RekaCoop_removed6-10-27_1920`). After the patch: `play` and `client` relinked with `mod/sync-game.ps1`, the mod rebuilt against the new interop with no errors, and a local join into the new tutorial world works with no warnings. Seen: the host had thick fog while the guest's sky was clear. The host's old test worlds are in `local/backup/host-before-tutorial-playtest-2026-10-27`.

## Testing on this PC
- `game/play` is the host copy (world in `CoopSaves/play`, in a town); `game/client` is the guest.
- Play session: `mod/play.ps1` (host on the primary monitor, guest on the second, both windowed at 80%; `-HostOnly`, then `-Join` later; `-Percent`). The host starts at the main menu. Window placement is `-coopscreen monitor:percent`.
- Start both: `mod/launch.ps1 -Instances play,client -ExtraArgs '-coopautoload'`. Commands for the dev console go into `cmd.request` in a game folder. See `mod/RekaCoop/Dev/Commands.cs`.
- Useful: `backport [go]`, `npcs` (nearby villagers, for comparing the two machines), `animals` (mirrors), `hut`/`hutprobe`, `hutctl` (take the hut's controls), `scanslow`, `poiparts`, `actions` (input bindings).

## Done since the Steam test
- Villagers near a guest are mirrored from the host like deer, within 80 m: position, facing, and the animator's parameters and states. They used to run their own AI on the guest and get dragged back. Positions now match exactly when standing and within 0.3 m when walking. Still villagers are sent once a second.
- Town freezes: the trial site's state took ~60 ms to capture and was checked every 30 s on both machines. Places are now checked by their parts alone (~2 ms) and captured whole only when a part changed. The scene scan no longer lists all 12,500 root objects in one frame (~65 ms), and big roots are split.
- Hut pieces were captured 120 times a second; now twice a second each.
- Mirrored animators no longer fight the guest's own transitions. Blends dropped from ~90/s to ~4/s in town.
- A guest at the hut's controls steers the host's hut. The host's own camera and controls aren't touched.
- Dialogue (0.3.6): guests follow the host's pace exactly; each line replaces the last at once and stays until the host moves on. The options list is gone; the host's pick shows as a line said by the host ("..." while choosing).
- Carrying looks right from outside: the animal takes the held pose, and the carrier's arms (IK rigs the puppet lacks) are streamed as bone rotations.
- A guest riding the hut while it walked 116 m stayed aboard. Backport's teleport lands a guest at the hut.
- The 35-50 ms hitches every ~30 s: the scene scan made a new array per root and type; it now reuses lists, and far stations and places are checked less often. Most 30 s windows now have no frame over 50 ms.
- Building at the same time: each player numbers blocks in their own lane. 200 and 300 fuzzed concurrent edits (dev `buildfuzz`) left identical houses; before, 200 left them a block apart.
- Guests startle deer like the host does (the host's flee triggers see the nearest player). Wild hares don't flee from anyone here; that looks like the game's own (pettable).
- Guest driving the hut with the host standing aboard: the game's control mode switched the floor off under the host; it's kept on now. Both rode ~500 m without moving on the floor.
- Shared pack (0.3.7): one inventory for everyone, kept in the host's save: items, money, building blocks and plans, clothes. Changes go out as deltas; the host applies a guest's, relays it, and every 3 s sends the whole pack so any drift is corrected. Tested with three copies, a late join, a rejoin, both sides changing at once, building costs and rewards.
- Pickups in tight piles (the Lutki planks) match by scene path first, then position.
- The other player's lantern shows its flame and glow.
- Tree and well loot bags open for guests too (they were mistaken for quest rewards, which only the host hands out).
- Ravens: each player's flying birds are streamed at 10 Hz and the others fly copies of them (model, animation, trail). The hand glow while aiming isn't sent.
- Conversations (0.3.8): whoever starts one leads it, and it runs once, on the host. A guest talking to a villager, starting a story dialogue or handing something in (berries to the wood creatures) has the host's copy do it. Guests within 35 m of the speaker are pulled in: real lines on their own dialogue box with the dialogue camera on their copy of the speaker. The leader picks the options; everyone else sees the same list, read-only. Anyone in the talk can continue a line, which runs the host's own next step (next line, options or end). Guests further away get subtitles.
- A place's saved state no longer puts carried things into the receiving player's hands (likely the wheel sliding into the host's hands).
- Perf log lines now show the whole frame (average, worst, count over 50 ms) next to the mod's own costs.
- Story state (0.3.9), from the evening playtest: skill and tool unlocks, tutorial steps and other saved progress follow the host (the guest never got raven fetch); story objects switched on or off by quest events follow the host (likely the invisible crows and Baba Jaga's book); both verified with dev commands, not yet in a real scene.
- Conversations (0.3.9): the host broadcasts the talk's state every second and guests clear anything left over; a guest's continue pressed while the line is still typing is no longer lost; a guest who doesn't choose within 45 s hands the choice to the host; a host more than 35 m from a guest's conversation keeps walking with their own camera (lines as subtitles).

- Desync detector (0.3.10): host and guest compare hashes of story flags, trials, saved progress, story objects and the pack every 15 s; anything that stays different is written to `desync.log` in the guest's game folder, key by key. Read it after a playtest.
- The tutorial's talking crows are now mirrored from the host (they had no id, so the guest's ran their own AI and stood elsewhere). Checked on a fresh local campaign: guest joined after the intro, crows in the same places, all categories matching through the prologue.
- Game hotfix 1.0.25 (0.3.11): BepInEx crashed the patched game at start (an Il2CppInterop hook found the wrong function); fixed in the plugin, and the package turns off BepInEx's Unity log listener. A guest's join could start the new-game intro instead of loading the host's world; fixed. Checked locally after the patch: join, all five desync checks matching, villagers and crows in place.
- Conversations (0.3.12), tested together on this PC: the starter steers and everyone is in it. Others are moved beside the starter, see the starter's camera, the starter's skipped typing and highlighted option; their own continue, skip and pick buttons do nothing. Puppets no longer show up in the other player's clothes.

## Not done
- The game's own way to take the hut's controls, on a guest: the test save is too early (hut control is still locked by the story). The steering underneath is tested with `hutctl`.
- A full quest/chapter playthrough together.
- Joining during a new game's intro (built 2026-10-27, tested with the intro simulated by `intro start|end`; not yet seen with a real new game): from `CanvasManager.ForbidPauseScreen_NewGameIntro` until the new world's first complete save, the host holds joiners and tells them "The host is starting a new journey"; then everyone connected gets the new world.

Details per system are in NOTES.md. The roadmap is at https://claude.ai/artifact/EQSFWeht8e2b6ckiUz22LM.
- 0.3.13 (AFK work, local only): the desync detector now repairs story objects and trial progress on the guest instead of only logging them (a guest-only trial tick was seen in a local run). The merchant's lost wheel is shared when carried (it had no id). Dev tools: `taskfn`, `tasklist`, `trace`/`circle` + `mod/synccheck.py` (walking sync: 2-3 cm p95), grounded `tp`. To try in tonight's playtest: the guest returning the merchant's wheel; reading `desync.log` afterwards for "switched"/"changed to match" lines.
