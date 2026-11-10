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

- [ ] M1 story/local split of pickup and placement events (AreaSync's split, generalized). Beats 6, 11, 14.
- [ ] M2 Interact intent for every story PlayerDetection, including ones wired at run time. Beat 15, quests.
- [ ] M3 guest silence list (quest tasks, QuestRunner, tracker evaluation, place events, travel triggers). Tracker part
      done in protocol 58; the rest as beats and the first generated quest need them.
- [ ] M4 nearest-player swap around story Updates on the host. Beat 16, quests.
- [ ] M5 a guest's carry/place/ritual player events as an intent. Beats 11, 12.
- [ ] M6 presentation of host story events on guests (music, ritual statics). Beat 18.
- [ ] M7 AreaSync adapters for the other physics triggers. Beats 10, 12.
- [ ] M8 UseItem intent (pine branch, lantern reveal, watering, fire). Beats 9, 13.
- [ ] M9 ritual advance entry. Beats 12 to 17.
- [ ] M10 hand-in intent for item-giving screens. First chapter after the tutorial.
- [ ] Talks as one session on every copy (seen by the user 2026-11-10, two symptoms, one cause). A copy that is not
      running the talk shows each of the host's lines as a new one-line dialogue (DialogueSync.FollowNow: force-close,
      start again). So the box blinks out and back on every line, and that copy's dialogue camera starts over with its
      opening shot on every line (measured in the indoor talk: guest leader `shot=Opening wide OTS reason=session
      start`, generation +2 per line). The host, which runs the real talk for a guest, validates no shot at all
      (`Player camera ... nothing validated`) and shows the guest's camera. Wanted: the host sends the whole talk when
      it starts, each copy runs it as one dialogue and only the line index is synced.
- [ ] The test saves were made with input off, so the lantern step's on-screen prompt (CTRL) was never satisfied. Has
      blocked nothing through beat 8; complete it properly in the fixtures before beat 9 (lantern reveal).
- [ ] Pickups can be taken twice when two players take the same one within a second (5 wood from 4 planks). Part of M1.
- [ ] Regression speed: one continuous playthrough per leader instead of a start per scenario, waits on conditions
      instead of fixed sleeps, digest on demand. Then, with the user's go-ahead, runs on Olga's PC over SSH.

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
