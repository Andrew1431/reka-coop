# Host-first plan (2026-11-09)

## Why fixes keep breaking other things

The transport, world transfer, puppets, looks, gear, shared pack, building, hut, weather and animal/villager mirrors are sound. They are either display or one bounded piece of state with one owner.

The story layer is the problem. Every copy runs the whole game, and the game is built for one player:
- 545 scene-wired UnityEvents across 291 types, and 167 methods that take a callback. None of that lives in saved state.
- One player, found through a handful of statics (`PlayerRoot`/`PlayerTransform` on GeneralAIController, QuestAi, HomePresence, LeadPlayerAlongRoute, DialogueCamera) and through detectors tagged "Player".

So a guest's copy fires its own triggers, advances its own tutorial and ticks its own tasks. The mod then reconciles two simulations: 99 Harmony patches, ten two-way state channels (story flags, unlocks, trials, stats, places, stations, conditions, tutorial, map, containers), 36 timing constants, and merge rules ("max of", "within 40 m", "ahead only", "3 s grace"). Each rule is right for the bug it fixed and wrong for some other scene. Examples from the notes: the self-repair reverting a guest's talisman, place sync putting the book back, ActiveSync switching a collider on inside a guest's arms, the gate softlock.

Two authorities cannot be merged reliably. The fix is to have one.

## The rules

1. **One story simulation: the host's.** A guest's copy shows the world; it does not decide anything in it.
2. **Guest to host: intents only.** "I interacted with X", "I walked into trigger Y", "I picked up Z", "I picked option 2", "I placed this block". Never state.
3. **Host to guest: state only, wholesale.** Captured with the game's own save code (45 ISaveable types, 15 map-element types, 32 place-element state types, the four flag tables) and applied as-is. No guest-to-host state, no merging, no distance rules. The desync detector repairs every category toward the host.
4. **Guests exist on the host as acting players.** When the host runs a guest's intent, or a guest is the one standing in a trigger or near an AI, the host's game sees that guest's puppet as "the player" (the statics above and the detector's target). This replaces moving the host's character (the gate fix) and the per-scene detector code.
5. **One-off effects are a stream, not state.** Dialogue lines, popups, sounds: host to guests, as now.

Per player and never shared: movement, camera, look, the mirror, own UI, tool aiming, what is in hand.
Unchanged: pack, building, carrying, pickups (first come), hut, weather, mirrors.

## Order of work

Each phase ends with the scripted run in phase 0 passing, host-led and guest-led. No new features until phase 4 is done.

**0. Regression run.** One script that drives the prologue on `play` + `client` with the existing dev commands (`walkto`, `interact`, `next`, `gates`, digest) and checks: every digest category equal, gate open, no softlock. Run twice: host leads, guest leads. This is what ends the loop; today every fix is checked by hand against one scene.

**1. Intent layer.** One message: (player, kind, scene path, args). Kinds: Interact, Enter, Exit, Collect, Pick, Next. Host runs the game's own entry point (`PlayerDetection.SimulateInteraction`, detector `OnEnter`, ...) with that guest as acting player. Folds in StoryInteract, DetectorSync, and the ten `Act.*` cases in DialogueSync.

**2. Acting player on the host.** Swap the player statics and detector target for the guest's puppet while an intent runs or while that guest is nearest. Verify each of the ~10 types that hold a player reference. Delete the "stand the host on the guest" code.

**3. Silence the guest's story.** On a guest, block at the source, by type: Player-tagged ObjectDetector events, PlayerDetection.OnTryToInteract (sent as an intent instead), TasksTracker evaluation, TutorialManager advancing, QuestRunner, POI_Event. Replaces the per-method prefixes.

**4. One-way state.** Channel by channel, once its guest actions arrive as intents: remove the guest-to-host half of StorySync, SaveableSync, TrialSync (gain and stats; the host counts them itself when it runs the intent), PoiSync, ConditionSync, TutorialSync, MapSync. Self-repair covers all categories.

**5. Play through.** Chapter by chapter with the phase 0 script extended as we go. Fix by adding an intent kind or a saved type, never a scene name.

## Remaining work as a checklist (2026-11-10)

From `research/story-survey.md` (172 rows of story machinery sorted by type; read from signatures, guesses marked there).
The game is one hand-scripted tutorial (20 beats, table in the survey; 1 to 4 done) and a procedural world built from
generic parts (35 quest task types, about 116 place types). So the rest of phases 3 and 4 is these mechanisms, each
proved on the tutorial beat that first needs it, not a plan per scene:

- [x] M1 story/local split of pickup and placement events (`CarryStory`: a carriable's pickup/drop events, a spot
      something carried is put into, items from the pack put into a place). Proved on the chicken, egg, offerings, embers.
- [ ] M2 Interact intent for every story PlayerDetection, including ones wired at run time. Beat 15, quests.
- [ ] M3 guest silence list (quest tasks, QuestRunner, tracker evaluation, place events, travel triggers). Tracker part
      done in protocol 58; the rest as beats and the first generated quest need them.
- [~] M4 story that looks for the local player every frame. The ritual's bones are done the other way round
      (`BoneSync`: the guest's copy looks, a bone it would start is an intent, the host starts its own); the host
      can't see what tool a guest holds when the tool lives in a place, not on the player. Quests may still need the swap.
- [x] M5 a guest's carry/place/ritual player events as an intent (pickup and drop through CarrySync, `Place`).
- [ ] M6 presentation of host story events on guests (music, ritual statics). Beat 18.
- [ ] M7 AreaSync adapters for the other physics triggers. Not needed through the ritual: `POI_PlayerAreaTrigger`
      (handing over the embers) only starts a dialogue, which a guest's copy already asks the host for.
- [x] M8 UseItem intent (`UseItemSync`: a spot finished with a tool; the rings swept with the pine branch).
      Lantern reveal, watering and fire use the same spots but are not exercised yet.
- [x] M9 the ritual advances on the host only (`RitualSync`); every step from the chicken to the house sitting
      down counted once in guest-led and host-led runs.
- [ ] M10 hand-in intent for item-giving screens. First chapter after the tutorial.
- [~] Talks as one session on every copy. Done for story talks (protocol 60): the host sends a whole dialogue once
      (`Start`) and then only the line number (`Index`); a guest runs it as one dialogue of its own, lines advanced by
      the box's own held continue action; a leading guest's press only asks the host. Measured on the guest: box
      hidden 21 times to 1 in the indoor talk, shots progress. Still line by line: any dialogue with a branch (villager
      greetings and answers through the options menu), passive lines, the rest of a talk after someone joins. Design,
      probe results and what is left (stages 5 to 8) in `research/dialogue-one-session.md`.
- [ ] The test saves were made with input off, so the lantern step's on-screen prompt (CTRL) was never satisfied. Has
      blocked nothing through beat 8; complete it properly in the fixtures before beat 9 (lantern reveal).
- [ ] Pickups can be taken twice when two players take the same one within a second (5 wood from 4 planks). Part of M1.
- [x] Regression speed: one continuous playthrough per leader, waits on conditions (done: 55 min to 15 for beats 1 to 8,
      both leaders). Scenarios still run alone from their own fixtures. Later, with the user's go-ahead: Olga's PC over SSH.

- [ ] An unanswered popup on the host (a tool unlock) blocks every interaction there, including the ones the host runs
      for a guest's press: nothing happens and nothing says why. Seen in scripted runs (input off); in real play the
      host just has to click OK, but the guest can't know. Run a guest's interaction past the block, or tell the guest.
- [x] (2026-11-12, see Progress) The talk camera is often wrong on the copy that does not lead the talk (reported by the user 2026-11-11, embers
      talk "Pfft, don't look at me like that": the host frames the old woman, the guest looks at its own player's back
      from behind with her out of frame). Many talks. Not urgent.
- [ ] Playtest notes from the user, 2026-11-11 (hut after the tutorial, both windows on one PC; not fixed yet):
      - The bread oven's cooking countdown differs between the copies (host 23, guest 12 at the same moment); the
        finished meal then appears on both. StationSync leaves running timers out of its comparison (`TimerRx`), so
        each copy counts down from whenever it got the station's state. Wants a pass on the oven as a whole.
      - While the host drives the hut, the guest loses all input and can't move. That was bearable when the guest's
        camera followed the host's driving view; now the guest is stuck looking at its own still camera. Either give
        the guest the driving view again or let it move and look around aboard (`watchingDrive` in HutSync).
      - Travel by driving into the fog (host drives): the guest also got "Do you want to leave this map and travel
        onwards?" (PopupSync mirrors it with a Continue button); it should be the driver's alone. After the host
        answered, the Travel UI's one-line talk "*happy clucking*" was led by the guest ("conversation led by
        playtest-guest", log line "one session 16 ... leading" on the guest), so the host could not click it on,
        and the host, seated at the controls, was "moved beside the starter". Talks the travel screen starts must be
        the driver's.
      - Same session: "playtest-guest asked for Say at Home[0]/PlatformFollower[9]/BB_Chicken_Skeleton_Chair(Clone)_
        00:02:27.7450000[32]/TalkableVillager[3], not found here": the chair's name carries a time stamp that
        differs per copy, so a guest cannot talk to the chair (ScenePath by name fails for built pieces).
      - The user's overall word: "Stuff feels jank but it does also all feel like it stays synchronized better";
        "Travelling between maps was very jank indeed, eventually all synced up". The session reached the Lutki
        place on the forest spirit's map. Its save is kept at `local/saves-playtest/andrew-2026-11-11` (and still in
        `game/play/CoopSaves/playtest`), with both logs beside it, to come back to.
      - **Questions to ask the user when they are back** (they said they will answer):
        1. What exactly was jank about the travel: the order of things, the waits, what each window showed?
        2. Talking to the elder Lutki as the host: did the guest follow, and where was its camera?
        3. Could the guest start a talk with the elder Lutki at all?
        4. Giving a small Lutki an item: did it leave the shared pack on both windows?
        5. A guest freeing a stuck Lutki or shooting a trap / high stand: did the loot card on the host get in
           either player's way?
        6. Does the journal or the elder say what the stuck-Lutki quest needs beyond freeing all three (in tests
           that alone did not count as a favour)?
        7. The food screen before travel: what did it ask for and what did it accept?
        User's leaning on the drive (to discuss later, nothing decided): lock everyone's movement and have them follow the
        host's drive, as the simple and safe option. The dream version is smooth hut movement with either player
        able to drive, but the hut's two-legged walk is shaky by nature, so walking around aboard would likely look
        ugly and cause trouble.
- [ ] Guests still send `GenericPOICondition` changes to the host. The host now waits 2 s and only takes a guest's
      word if its own copy has not got there (it used to run what hangs on the condition twice). Remove once the
      Lutki's planks no longer need it (phase 4).
- [ ] Who leads a talk nobody set off (she walks to the next bowl and calls): the host. A guest can't click it on.

Order: tutorial beats 5 to 20 in sequence with a regression scenario each (that covers M1, M5 to M9), then one
generated quest of each task family on the first map (M2 to M4, M10), then phase 4 removals, then phase 5.

## Risks

- IL2CPP: method bodies aren't readable, only types and signatures. Whether a type reads the player static each time or caches it has to be probed per type (phase 2).
- The host's game may cull or throttle logic far from the host's own player (`AnimatorDistanceThrottle` does for animation). If a place doesn't run with only a guest there, it needs to be kept awake for acting players.
- Callbacks held in memory mid-scene are lost if the host reloads. Same as single player.
- Guest actions gain a round trip. Fine on Steam relay for interactions; movement stays local.

## Progress

- 2026-11-09 **Phase 0 done.** `python mod/regress.py` (fixture `local/saves/pre-door-1.0.32`, a new game just after the intro; steps to make one are in the script's header). Scenario `door`: both pass, host-led and guest-led. Found on the way: `TalkCamera` threw once per session (`Application.onBeforeRender` is stripped; hook removed), and a guest who crossed any detector in the last 10 s leads a talk the host started (goes with phase 2).
- 2026-11-09 **Phase 1 done.** `Sync/Intents.cs`, message `Intent` (protocol 55): Interact, Enter, Call (the eight entry points a guest's copy still reaches by itself), DefaultTalk. `Intents.Actor` is the guest whose intent is running. Pick and Next stay with the conversation (leader only). Behaviour unchanged; the run passes both ways.
- 2026-11-09 **Phase 2 done.** `Sync/ActingPlayer.cs`: while one AI updates (and while a guest's intent or a detector ticked for a guest runs), the game's shared player references (`TransformReference` assets `PlayerTransformReference` and `PlayerHead`, their `.transform` field) point at that guest's puppet. `GeneralAIController.s_playerRef` is not what the AI's getters read; replacing it does nothing. `Sync/AreaSync.cs` (protocol 56, intents AreaEnter and AreaExit): `PlayerTrigger` is a physics trigger a puppet never trips, and it is what starts the old woman's door talk (`Player in area detection_ Outside Fence`), so a guest's copy reports walking in and out instead of running its events, and the host runs its own copy's; entered by the first player in, left by the last one out. "Stand the host on the guest" is deleted. The callback-less DefaultTalk fallback is still there but now logs a warning when used; the regression's guest-led run no longer reaches it. Regression passes both ways. Other physics triggers a puppet can't trip (`POI_PlayerAreaTrigger`, `CombinedTriggerElement`, `SlowZone`, `VillageDoor`) are not handled yet; do them in phase 3 as they come up.
- 2026-11-10 **Phase 3, first slice (the tutorial yard).** Protocol 57. `TutorialSync`: only the host's tutorial moves; a task done on a guest's copy is a `TutorialSolve` intent, the host solves its own current task, guests follow one step at a time (a block that a save loaded but did not start is started first: the host also sends whether its block is running). `AreaSync`: a `PlayerTrigger`'s calls are sorted per listener into story and local (camera, sound, compass); a guest's story calls are switched off at the source and sent as intents, local ones stay native on every copy. The old woman's `TutorialDialoguePiece.RunDialogue` never runs on a guest (the host's story runs it, with its callbacks; her "one more thing" comes when a player heads out of the yard, not when the journal closes). `ActiveSync` carries an NPC's interact prompt. `ConditionSync`: guests no longer send gate changes; when a guest shuts the yard gate the host is brought in too (inwards is the yard's own inside area, not the door's axes). Dev: `-coopnoinput` (keyboard, mouse, pads off in scripted runs), commands `areas`, `journal`, `pieces`. Scenario `yard` in `mod/regress.py` (door talk, in through the gate, her talk, the book, the three journal tasks, gate reopens, her next talk, popups); door and yard pass host-led and guest-led. Still to silence at the source: `TasksTracker` evaluation, `QuestRunner`, `POI_Event`, the physics triggers a puppet can't trip (`POI_PlayerAreaTrigger`, `CombinedTriggerElement`, `SlowZone`, `VillageDoor`, `TeleportPlayerHomeOnTriggerEnter`, `TriggerDayTimeChanger`), and the `Call` entries as plain interact intents.
- 2026-11-10 **Phase 3, tutorial beats 5 to 8** (gathering, the Lutki's planks, the talisman, teleporting home, her two
  talks). Protocol 58. `TrialSync`: the trial tracker is host-only: a guest's tasks are done when the host's are
  (`ProgressionTask.Evaluate` answers from the mirrored state), the tracker's story-event handlers and
  `OnFinishedQuestStep` on a guest are a `TrackerEvent` intent, and the guest-to-host progress channel (Gain) is gone;
  the digest repair takes the host's values wholesale. A guest's pickups still reach the host's tasks as stat deltas.
  Fixed a host crash from the yard slice: the hook on `PlayerTrigger.OnDisable` trapped inside GameAssembly the first
  time a trigger switched itself off (anyone coming back to the yard with the talisman). `SaveableSync`: a guest's
  place history now takes the host's map number. Dev: `patchaddrs` (every patched method with its address, to find a
  hook from a crash offset), `backport port`. Regression: scenarios `lutki` and `home`, fixtures `after-yard`,
  `talisman`, `pine` (local/saves), `--save=NAME`, and a locked log now stops the run instead of reading a stale one.
  Survey of all story machinery by type in `research/story-survey.md`; the checklist above comes from it.
- 2026-11-10 **Doors the story unlocks, and the chained regression.** Protocol 59. `ConditionSync`: the old woman's hut
  doors (`Tutorial_BabaHutLock`) open on a story event only the host hears, so a guest stood at a locked door and
  "talked to her inside" from 4 m through it; the host's open/locked now go to guests on change and on arrival. Found
  by the user watching a test window. `mod/regress.py`: the default is one chained playthrough per leader (door, yard,
  lutki, home in one session, checks between steps); a named scenario still runs alone from its fixture; `--each`,
  `--fixtures` (saves the host's world before each step as that step's fixture); `until()`/`walk()` wait on conditions;
  `home` checks the leader is inside the house. Dev commands no longer throw when `cmd.request` is still being written.
  Design for talks as one session per copy in `research/dialogue-one-session.md` (not started; eight probes first).
- 2026-11-10 **Story talks as one session (protocol 60) and the talk leader.** `DialogueSync`: see the checklist item
  above; dev probes in `Dev/DialogueProbe.cs` (`dlgtrace`, `dlgstate`, `dlgone` and others). `AreaSync`: the host
  walking into a trigger with story on it is noted as the host's own step, so a guest's last walk into some detector
  (the Lutki wander through theirs once a second) no longer leads the talk the host sets off and pulls the host 100 m.
  Chained regression passes both ways (156 checks, 15.8 min).
- 2026-11-10 **Second test bed, game 1.0.40.** Olga's PC runs the same suite over SSH (`mod/olga.ps1`: deploy, run,
  wait, stop, allow; one-click stop on both desktops and in taskdeck). `mod/sync-game.ps1` relinks the copies after a
  game patch (long paths). The mod needed no change for 1.0.40.
- 2026-11-10 **The whole ritual, tutorial tasks 5 to 16** (protocols 61 to 64). New: `CarryStory` (what picking up,
  putting down and placing set off runs on the host for the guest who did it), `RitualSync`, `UseItemSync`, `BoneSync`,
  the `Place` intent, a line-of-sight detector taken on a guest's word, places host to guests only (`PoiSync`), a
  guest's condition deferred on the host, deliberate acts outranking detector walk-ins for who leads a talk, a tool's
  own parts and run-time clones left out of the on/off comparison. Villager talks run as one session too (merged
  from a side branch, dialogue stages 5 and 7). Regression scenarios `villager`, `chicken`, `cleanse`, `egg`,
  `offerings`, `bones`, `embers` (through the finale) with fixtures `*-1.0.40`; `--start=<fixture>` leaves both games up there.
  After the ritual the building lesson is skipped in co-op, both players can build, and the trial ("Trial of the
  Chicken", all 19 tasks) completes on both copies, guest-led and host-led. Also: a guest's talisman goes back to
  "home" with the host's after the ritual, and the game's ritual-finished switch is set on guests. Next: leaving
  the first map in the house, then generated quests; the three open items above.
- 2026-11-11 **Shared playthrough and the first travel.** Regression has a third playthrough, "shared, taking
  turns": the players swap who starts each step and split the step (branch / chicken, egg carried / put in the oven,
  first bowl / grain and second bowl, both charms on the bones, embers talk / embers fetched). Passes locally; the
  two single-leader playthroughs pass on Olga's PC on the final build. New scenario `travel` (fixture `hut-1.0.40`,
  after the trial): a guest's try is refused, the host's takes everyone to the first generated map. Fixed on the
  way: a guest could travel alone within two minutes of loading the host's world; a guest counted the new map
  twice (`PoiMapHistoryTracker`); characters gone with the old map threw in `AiSync`; roads are left out of
  ActiveSync (a generated map names them differently from a loaded one). Wildlife the map spawns around a player
  (`ODPOI_`) is each copy's own for now: not synced, a guest's hare notices the guest itself. Seen, not looked
  into: "Stations: no PlantGrowth ... here; dropped its state" for five far-away plants on the guest after travel.
- 2026-11-11 **Travel the game's own way; the forest spirit's trial starts in co-op.** `travel` now goes through
  `MapBorderFogManager.TriggerTravel` (the question at the map's edge), the travel screen (`BB_Logic_TravelStation`,
  one free destination after the tutorial: `Chapter01_MainQuest_01_LeshyFirstOffering`) and `UI_TravelStation.TravelOnwards`.
  The dev `travel` command (`JumpToNewMap`) lands on a plain map with no trial. Found: the new trial's title card
  and intro talk start the moment the host arrives and the game can't save while they are up, so the arrival save
  never went out and the guest heard the intro from the old map. The host now holds the title card
  (`WorldTransfer.HoldForGuests`) until every guest stands in the new world, three minutes at most. Trial 1
  ("Trial of the Forest Spirit": three favours, then his trial) begins on both. Open: any other window open on
  the host at arrival still delays the guests' save; the hint "walk your house into the fog" does this in the
  scripted run. Not exercised: sitting in the skeleton chair and walking the house to the edge (dev `hutctl`
  leaves the player stuck after travel, so the scenario calls TriggerTravel directly); the food screen (three
  meals, `FoodSelectionUI`; TravelOnwards went through with none chosen, real play may not allow that); cooking.
  Dev commands `items [part]` and `travelui [food|pick|confirm|dest|go|open|close]`. Olga's PC, build of this
  morning plus the first travel scenario: guest-led and shared playthroughs pass, host-led failed only on a
  guest's request for an old-map deer that arrived as the host left (the check now starts at the guest's arrival).
- 2026-11-11 **First favours to the forest spirit (trial 1), fixture `leshy-1.0.40`.** The map's quest machinery is
  `QuestGenerator` (10), `FoodVillagerLogic` (5), `POI_Logic_RequestItems` (2) and small places; no `QuestRunner`
  on this map. Favours are `LeshyApproval` statics set by `LeshyApprovalHelper.CompleteTask` (dev `leshy`,
  `leshy all`): freedHares, destroyedHighStand, helpedStuckLutki, helpedLutki, charmedDeer, madeOffering,
  helpedMara. Works in co-op, host-led, guest-led and shared: trapped hares (scenario `hares`; the first favour
  brings the spirit's first words), three high stands destroyed (tried by hand: guest, host, guest; no scenario
  yet), three stuck Lutki pulled out (scenario `stuck`). Fixed for the stuck Lutki (M2, first part):
  `StoryInteract` now also sends a guest's press to the host when it counts, solves or pays out
  (`CountUpAndDoSth_StaticallySynced`, `PoiQuestInterface`, `POI_Loot`, `LeshyApprovalHelper`, `QuestPageHelper`);
  `ScenePath` puts the place's id on a root that is a place (`POI_TrappedHare(Clone)#<id>[0]`), since a generated
  map has the same place several times and the host freed the wrong Lutki; a guest's press on something the host
  has already completed no longer switches it back on (`DialogueSync.Admit`); a guest's visit to a place is
  recorded in the host's `PoiMapHistoryTracker`. Open: freeing all three stuck Lutki did not set
  helpedStuckLutki (a further step, not found yet); the deer favour (the dev interact reached a deer 29 m away
  and nothing counted); the loot card for something a guest did opens on the host too and blocks the host's
  copy until claimed (the open item above, now easy to hit); ActiveSync skips roots whose name repeats, so
  repeated places rely on PoiSync alone. Olga's PC (paused by the user since): host-led and shared playthroughs
  pass with the new travel; guest-led failed only "the bones are gone" (25 of 28 after three laps; now five).
- 2026-11-11 (evening) **Suite about twice as fast; playtest fixes; the Lutki elder's quests.** A playthrough of all
  14 steps now takes 21 to 23 minutes (was about 29 for 11): dev `talkthrough` (the game clicks a talk through),
  commands picked up every frame and matched whole, condition waits, teleports checked. The speed exposed races
  in the script, each fixed there: acting before a tutorial prompt is up, a villager's call on leaving the
  village, a teleport undone by a talk just ending, `say`/`interact` not counting as the host's own press. With
  those: guest-led passed twice and shared once on Olga's PC, shared once here; host-led not yet clean (result
  pending on Olga's PC). Runs are split between the PCs now, never the same playthrough on both.
  Mod fixes: a guest's interaction runs on the host past an open window there (`StoryInteract.Run`); while the
  host drives the hut the driver leads every talk; the travel question is not mirrored to guests; built pieces'
  per-copy time stamp is left out of scene paths (the chair; not verified by hand).
  The elder Lutki (dev `say Elder`): "Can I help you?" gives the high-stands quest; "What are you doing here?"
  the beekeeper's log hive (helpedLutki); "Are there many of you?" then "Crushed and burned? ..." starts the
  stuck-Lutki quest. Dev `counts`: pulling a Lutki out raises `StuckLutki_FoundStuckLutki` only;
  `poi_LutkiStuckInLogPile_numberOfFreedLutki` (0/3) stays 0, and the elder then says "there's more got stuck
  again". The freed Lutki has a `BirdInteraction` (`BirdsHolding_01`): most likely the bird tool has to be used on
  it before it counts as freed. Not tried. Also found: `CountUpAndDoSth_StaticallySynced.syncedCounts` is a
  static per copy and is not sent to guests (host 3, guest 0 after three pulls); the host runs the story so
  nothing broke, but a guest's copy has the wrong counts. To sync with the other flags (StorySync).
- 2026-11-11 (night) **Stopped here; both PCs handed back to the user.** Olga's PC is paused (`olga.ps1 stop`):
  do not run anything there until the user says it is free, then `olga.ps1 allow`. No test games on this PC
  either until the user says so. HEAD is the build both PCs last had (protocol 65).
  **To pick up, in this order:**
  1. Host-led playthrough is not clean yet. Last failure (twice: here, then Olga's PC): the offerings step pressed
     "honeycomb" before the near bowl took offerings, and the dev `interact` reached the far bowl 16 m away. The
     step now waits for the near bowl; that fix was started on `offerings host` here and cut short, so it is
     unverified. Run `regress.py offerings host`, then a host-led playthrough.
  2. Then one confirmation round, split: host-led and shared on one PC, guest-led on the other. Passed so far on
     today's fast suite: guest-led twice and shared once (Olga's PC), shared twice (here), host-led once (Olga's
     PC, before the last two mod changes).
  3. Ask the user the seven playtest questions (open items above) and decide the passenger view while the host
     drives.
  4. Verify by hand that a guest can talk to the skeleton chair (fix in, never exercised).
  5. Stuck-Lutki favour: try the bird tool on a freed Lutki (`BirdInteraction` on `BirdsHolding_01`), watching
     `counts Freed` on both copies; then the elder's "I found some Lutki ..." line again.
  6. The oven pass (countdown differs per copy), the dialogue camera on the non-leading copy.
  7. A scenario for high stands (three destroyed, mixed players) and one that drives the hut for travel.
  8. Release 0.4.1 only with the user's go-ahead, after their next playtest.
- 2026-11-12 **The user's answers to the playtest questions** (asked in chat):
  1. Travel: the guest has no loading screen or indicator; it sees the world morph under its feet, the weather
     change, lag; on arrival it runs at about 1 fps while things settle, the dialogue starts while that is still
     going, then everything comes together and runs fine. To do: cover the guest's screen for the change, and hold
     the arrival talk until the guest's game has settled, not only loaded.
  2. The elder Lutki's talk as host went fine on the guest. The camera is wrong in ALL dialogues ("still a major
     issue"): a major item, not polish.
  3. A guest starting a talk with the elder and picking options worked fine.
  4. Giving a Lutki an item: not tried (did not have the item).
  5. Loot cards in the other player's way: not tried.
  6. What the stuck-Lutki quest needs: not known to the user.
  7. The food screen took three berry soups without trouble. Reminded again that the guest saw the travel
     question, which should be the host's alone for now (fixed 2026-11-11, to confirm in the next playtest).
  Still to decide with the user: what a passenger sees while the host drives.
- 2026-11-12, later. Host-led playthrough passed here (24 min); shared passed on Olga's PC. Guest-led failed there
  at the bones (24 of 28 gone after five rounds, so the embers after it failed too), plus four "native->managed
  trampoline" errors on both copies; the next run overwrote the logs. Bones guest-led passed 3 of 3 in isolation
  here, so it needs the chained run. The suite now keeps a failed run's logs in local/logs.
  Travel for the guest (answer 1): the guest's screen is covered from the host's departure until its own game has
  loaded the new map and run smoothly for 2 s (about a minute in the test); it then tells the host, and only then
  does the host show the held trial card and the old woman's words. Found on the way: the hold never held. It looked
  guests up by player id in a table keyed by connection and released the card as soon as the world was sent.
  Fixed; the travel scenario checks cover, settled message and order. Protocol 66.
- 2026-11-12, afternoon. Guest-led passed on Olga's PC on the second try (22 min): all three playthroughs passed
  once today; the bones failure is intermittent and still uncaught (logs are kept the next time).
  **Talk camera fixed** (answer 2, the "major issue in all dialogues"). The watching copy did get the leader's pose
  and did switch its camera brain off, but the game also runs the brain by hand (`CinemachineBrain.ManualUpdate`)
  late in the frame, which put that copy's own dialogue shot back on the camera. Prefix on ManualUpdate while
  someone else's camera is shown (`BrainYieldsToShownCamera` in TalkCamera.cs). Probed line by line: both cameras
  identical. Suite: `same_view` check in the bones and embers talks; embers passed host-led, guest-led and shared.
  The same fix makes a passenger see the host's driving view in the hut (checked: cameras equal during the drive,
  the passenger's own camera back after), which is what the user leaned towards; to confirm in their playtest.
  Stuck-Lutki favour, second stage: still unknown. All three pulled out (found counter 3) and the elder told twice
  ("There's more got stuck again"): no favour, `poi_LutkiStuckInLogPile_numberOfFreedLutki` stays 0/3. The freed
  Lutki has a bird "Hold" spot (`BirdInteraction`, GenericPOICondition) and takes an item (TakeItemFromPlayer);
  what counts one as freed is not visible from the events. Parked; best learned from a real playthrough.
- 2026-11-12, later still. Confirmation on the camera build: host-led passed here (23.8 min). Shared failed on
  Olga's PC at the offerings, and the kept logs finally show why this step fails now and then: it is the script.
  The old woman's one-line call from the second bowl ("Into the bowl with it!") sometimes starts just after the
  script has checked for it; nobody clicks it, it stands open on the host, and the grain bowl only switches on when
  that line ends. Not a mod fault. The script now ends that line whenever it shows up while it waits for the bowl.
  The four "native->managed trampoline" errors in that run came after the failure (a NullReference in game code
  while the story was out of step); to look at again only if they show up in a clean run.
- 2026-11-12, evening. After the offerings script fix: shared passed on Olga's PC; guest-led failed there at the
  stuck Lutki (the guest's first pull was admitted by the host, "starts Interact", and then nothing happened; the
  host pulled that same one itself in the next turn). In the kept log the host had just started the passing remark
  "Is there a Lutk stuck in that log pile?" when the guest pulled. Guess: the game ignores a use while a remark is
  on the host's screen, like it does with a window open. StoryInteract.Run now runs the use directly in that case
  too. NOT verified: three tries here did not hit the remark at the moment of the pull. To watch in later runs
  (log line "Story: a remark is showing here; running ... past it").
  Today's score on playthroughs: host-led 2 of 2, shared 2 of 3 (one script fault, fixed), guest-led 1 of 3 (bones
  once, uncaught; stuck Lutki once, above).
- 2026-11-12 playtest notes (user, kept save, both windows on one PC; build 4ff of today, protocol 66):
  - A high stand destroyed on the guest: the host saw the quest's progress. Works.
  - Loot card left open on the host, guest dismissed its own and talked to the elder Lutki: worked; the host was
    pulled into the talk with the card still on top of it. Camera identical on both in the screenshot. The user asks
    whether dismissing the card should be synced. Idea to offer: don't sync the dismissal; show the card only to
    the player who earned it and a small non-blocking notice to the other.
  - The reverse (card open on the guest, host carries on): worked. Camera sync "looks good".
  - Host drives the hut: the guest sees the host's driving view. But the hut's walk animation on the guest keeps
    restarting ("the bird keeps entering the start of the animation, so it is just shuffling"). To fix.
  - Talking to the skeleton chair pulled the guest all the way in; the user accepts that ("we don't want the client
    running around doing things while the host is driving").
  - Travel: the guest's cover screen worked; the load "took very long" for the guest (log: cover up 57 s from the
    host's departure, the trial card shown only after the guest reported settled). State check after arrival, both
    games still up: story, trials, saveables, active objects and pack all equal on host and guest; no errors in
    either log; one desync on the guest (two log hives' item displays) corrected itself.
  - Open after this playtest: hut walk animation on the passenger; the reward card for the player who did not earn
    it (offer: small notice instead of a blocking card); shorten the guest's wait on travel if possible (the 57 s
    includes the host's own load and save before the world is even sent).
  Olga's PC stopped answering (ssh timed out) during the last guest-led run; result unknown.
- 2026-11-12 **Pre-release audit** (asked by the user: F8 reports, join/leave/rejoin, launch flags, save backups).
  1. F8 reports. The box, the bundle and the fan-out to everyone in the session work (tested: same report id on
     host and guest, scrubbed json.gz in BepInEx/RekaCoop-reports/outbox). But `Reporter.DefaultEndpoint` is empty:
     server/log-reporter was never deployed, so nothing is ever sent, while the game said "logs are on their way"
     and the readme said reports are sent. Fixed for now by telling the truth: no upload notice without an endpoint,
     the F8 message says where the file is and to post it on Discord, readmes rewritten. "F8: report a problem" is
     now in the top-left status line; long notices wrap instead of being cut off. DECISION FOR THE USER: deploy the
     Worker to their Cloudflare account (then set DefaultEndpoint and put the upload wording back) or ship as is.
  2. Join/leave. Reviewed CoopDriver/SteamLobby/CoopSession/SaveIsolation. Found and fixed:
     - A join that fails (lobby full or gone, host never answers) left the player a "guest" at the menu for good:
       every menu button said "You're joining a friend's world" until a restart. Now gives up (20 s for the lobby,
       100 s for the host), says why, and goes back to the player's own menu and saves.
     - Steam starting the game from an invite ("Join Game" with Reka closed) handed the invite over before the main
       menu was up; the code then answered "go back to the main menu first". Now held until the menu is up.
       NOT testable here (needs two Steam accounts); to try with a friend.
     - A guest sent out of the host's world goes through the pause menu object, which does not exist in the first
       seconds after a world loads; now retried every second until the guest is at the menu.
     Checked and fine: host with no flags hosts over Steam once in a world, lobby closes at the menu (run in a
     sandbox copy without any co-op option); guest back to the menu leaves cleanly, host carries on; host to the
     menu sends guests home; version mismatch says so. Not testable locally: everything that needs a second
     Steam account (invite, Join Game, rejoin, switching host).
  3. Launch flags. None needed: with no co-op option the game is "Auto" (hosts over Steam when in a world, becomes
     a guest on an invite, and back). The -coop... options are dev only; the dev commands are not in the release
     build. Two player messages still said "join by IP (-coopconnect)"; removed.
  4. Save backups. Was: one zip per start, newest 10, plus before-coop forever. Added: a zip every 20 minutes
     while playing in one's own world (newest 12, never while the game is saving, off the game's frame), an
     "Open Save Backups.bat" that opens the folder and prints the restore steps, and step-by-step restore
     instructions in both readmes. Tested in a sandbox copy (start backup and playing backup made, 152 KB, 9-80 ms).
  Also: package.ps1's default game version was still 1.0.25; now 1.0.40. A release-style package builds
  (local/release/RekaCoop-0.4.1-audit.zip, not published).
- 2026-11-12 **Report server deployed** (the user chose to deploy; their Cloudflare account, wrangler already
  logged in). https://reka-coop-logs.andrew-hartwig1.workers.dev, Worker `reka-coop-logs`. The account has no R2,
  so (the user's choice) it stores in Workers KV instead: namespace REPORTS, reports expire after 90 days, free
  tier 1 GB and 1000 writes a day (past that: 503, the mod retries on a later start). Admin token in
  `local/reka-logs-admin-token.txt` (not in git); `server/log-reporter/pull.py pull/group/show` reads them.
  Mod: `Reporter.DefaultEndpoint` set; an empty Endpoint in an old config means the default; upload wording and
  the first-start notice are back (the notice now says 90 days). Tested for real with two dev copies: F8 report
  from host and guest arrive under one id; pulled and shown with pull.py. Found on the way: an old backlog went
  first and ran into the rate limit (6 a minute per address), so the fresh F8 report waited for a later launch.
  Uploader now sends F8 reports first, then newest first, one every 12 s, and waits a minute on "slow down".
  About 46 test reports from today's dev copies are on the server (they expire by themselves).
