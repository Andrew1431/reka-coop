# Reka co-op notes

Roadmap artifact: https://claude.ai/artifact/EQSFWeht8e2b6ckiUz22LM (source: roadmap.html)
Playbook: https://github.com/cranebeaker/coop-anything (AGENTS.md). Personal BepInEx IL2CPP workflow: ~/.claude/skills/bigwalkmodding.

## Identity (2026-10-26)
- Steam appid 1737870, buildid 26200396 (1.0 launch day)
- Unity 6000.1.17f1, IL2CPP x64, global-metadata v31 (unencrypted)
- Reka.exe SHA-256 CC70C5F73AEFEF95275CAF601165DBD761B71CBC9C1402A15C303B39F423749A
- GameAssembly.dll SHA-256 9FE962C63301FC710B27E95ABD0AE0831268B8F27DF244C49260E3D79419DC4F

## Observations (static, metadata strings only)
- No networking lib. Facepunch.Steamworks bundled (SteamMatchmaking, SteamNetworkingSockets present as types; runtime use unverified). Game classes: SteamWorksManager, SteamLaunchGuard.
- Libs: Rewired (obfuscated), FMOD 2.2.23, Addressables 2.7.0, UniTask, DOTween, Newtonsoft.Json, Cinemachine, HDRP.
- Saves: %AppData%\RekaGame\Saves\<slot>\ {Meta,House,Inventory,Character,Progress,PoiHistory,Collection,Guidance,Stats,Thumbnail,FogMap,HeightMap}.xml + SaveLog.txt; likely XmlSerializer.
- Scenes: MainMenu, GameScene. Procedural world (EnvironmentGenManager, MapLoader, ChapterGenerationSettings). No combat.
- Hook candidates: Player, PlayerMovement_2021, CharacterAssembler, ChickenLegsBehaviour, ChildPlayerWhenStandingOnHouse, BB_Building, HouseBuildRegistry, InventoryManager, DaytimeManager, WeatherManager, TasksTracker, Quest, ChapterState, NPC_DialogueManager, SaveLoadGame, SaveSystem.
- Many Debug*Section / Marketing*Tab debug-cheat panels registered at startup.

## Tooling
- BepInEx 6 IL2CPP bleeding edge >= be.787 (latest be.791, 2026-10-23); < be.755 fails on metadata v31.
- Upstream Il2CppDumper fails on v31; use Cpp2IL or BepInEx interop output.

## Workspace (2026-10-26)
- game/host and game/client: hardlinked copies of the Steam install (fsutil confirms 3 links per file). Never edit a pre-existing game file in place there (writes through to the real install); replace by delete+copy to break the link first.
- BepInEx 6.0.0-be.791+b782da4 Unity.IL2CPP win-x64, zip in vendor/bepinex-be791, SHA-256 bb1c510909b69522733791f3a0817a487379a52da6468a492f1c9c19b3ec6f9a. Extracted with `unzip -n` (no overwrite).
- steam_appid.txt (1737870) in each copy so Steam doesn't relaunch the real install.

- First launch (host copy): BepInEx be.791 chainloader OK, interop generated (143 files; 15022 methods unstripped, 957 failed). Then SteamWorksManager.Awake -> SteamClient.Init failed 3x "ConnectToGlobalUser failed" and SteamLaunchGuard.MayRunAfterInit shut the game down by design. Cause (derived): account was borrowing Reka via Steam Family Sharing while the license was in use. Game will not run without a valid owning Steam client.
- After purchase (own license): host and client copies both ran at once, each launched directly with `-logFile <copy>\player-<role>.log`. Both logged "Successfully Initialized steamworks on attempt: 1", reached the main menu (MainMenuAIHost), and were Responding after 20 s. No single-instance block. Same Steam account in both.
- Memory: host ~5 GB, client ~9.5 GB working set at main menu (client was still settling); 2.8 GB of 31.9 GB free. RAM is the limit for 3+ instances.
- Not yet tested: loading into a save with two instances (both would share %AppData%\RekaGame\Saves — redirect in plugin before doing this).
- Unity Player.log is shared across instances: %USERPROFILE%\AppData\LocalLow\Emberstorm Entertainment UG (haftungsbeschränkt)\Reka\Player.log

## Mod (mod/)
- build.ps1 builds RekaCoop (+LiteNetLib 2.1.4 netstandard2.1) into every game/* copy. launch.ps1 snapshots PlayerPrefs, starts copies (first `-coophost 27877`, rest `-coopconnect 127.0.0.1:27877`, all `-coopinstance <name>`, windowed 1280x720, own -logFile). stop.ps1 stops only game/* copies and restores PlayerPrefs.
- Test copies share PlayerPrefs (registry HKCU\Software\Emberstorm...\Reka: graphics, audio, Rewired keybinds, MOST_RECENT_SAVE) with the real game. Pristine backup: local/playerprefs-pristine.reg.
- Save isolation: `FileIO.SaveRootOverride` (game's own static) set in Plugin.Load. Verified: SaveFilesRootPath and BlueprintLibraryPath resolve to game/<copy>/CoopSaves/<name>/.
- Verified 2026-10-26: host listened on UDP 27877, client connected over loopback, Hello/HelloAck done (client = player 1), ping running.

- Dev flags: `-coopautoload` (loads most recent save from main menu), `-coopbotwalk` (circles via PlayerMovement_2021.DebugMoveInputOverride; works while unfocused), `-cooptile i/n` (windowed, tiled across primary monitor; game re-applies its fullscreen pref so this re-checks every second). `mod/shot.ps1` drops shot.request -> each copy writes game/<copy>/shot.png from its own back buffer (works when covered).
- BepInEx console disabled in both copies ([Logging.Console] Enabled = false).
- Verified 2026-10-26 (protocol v2): both copies autoload the same save; client bot walks; host sees client's puppet walking (diag: 5/6 renderers visible, ~5 m away, screenshot confirmed). Puppet = clone of local `Reka_Body_Base_01` under an inactive parent, all components except Transform/Animator/renderers destroyed. 15 float + 23 bool animator params by index + per-layer fullPathHash crossfade. 15 Hz, Sequenced, 100 ms interpolation.
- Gotcha: CharacterAssembler adds body/head/clothes SMRs (children of Reka_Body_Base_01, rootBone root/root.x) several seconds after PlayerMovement_2021 exists. Cloning earlier gives an invisible puppet; PlayerSync waits until SMRs have existed 2 s.
- Puppet lacks: backpack/lantern/tools (siblings under PlayerCameraSetup), own appearance (it copies the local look), MagicaCloth, IK rigs, FMOD footsteps.
- Same save on both -> both players spawn at the same spot.

## Appearance (protocol v3)
- Look = SaveGame.SaveableCharacter (Character.xml, ~1 KB: wearable mesh/material/texture IDs + material float sliders). Mod copy: Appearance/AppearanceData (Unity-free, XML + net).
- Profile: BepInEx/config/RekaCoop/appearance.xml per player. Missing -> import newest Character.xml under FileIO.SaveFilesRootPath; none -> first capture of the worn look. `-cooprandomlook` calls CharacterAssembler.RandomizeLook() and saves that.
- Capture: SaveLoadCharacterCustomisation.GetCharacterState(new SaveGame()). Apply: `SaveableCharacter.ToGenerationDefinition(pRef, assembler.cbd)` then `CharacterAssembler.DestroyAllWearableMeshes()` + `SpawnFromGenerationDefinition(def)` (async). RestoreCharacterState does NOT re-dress in the world (only during load) — it silently did nothing and the save look overwrote the profile; guard now refuses to save a profile that didn't stick.
- Puppet keeps CharacterAssembler; ApplyLook destroys copied "(Clone)" wearable children, then spawns the remote look. Verified: client random look (vest, crown, braid...) built on host's puppet 9/9 renderers; profile survived a restart and was worn again.
- Host keeps the latest look per player and replays them on PlayerJoined; looks are re-captured every 2 s and re-sent on change.
- Verified with Andrew's own character (2026-10-26): made via New Game in the client copy, Character.xml copied to the client profile, that save moved to local/backup/client-own-character-save. Both windows show both characters correctly. A "did not stick" warning still fired (look visibly applied) — mismatch-profile.xml / mismatch-worn.xml dump added to diagnose; likely formatting/version differences between raw game XML and captured state.
- Not yet: host-side sidecar per Steam ID, character-creator flow for players with no saves, unlocked-items check.
- Bug fixed (0.3.1): a first-time guest imported "the newest Character.xml under SaveFilesRootPath", which for a guest is the sandbox holding the host's world, so it became the host's twin (and saved that as its profile); with no save it captured the worn look, also the host's. Now a real guest imports from `%AppData%\RekaGame\Saves` (read only), a dev guest copy from nothing, and a guest with no look of its own gets `CharacterAssembler.RandomizeLook()` once, saved as its profile. Verified in the client copy with its profile set aside: randomized, sent, saved; profile restored afterwards.

## World sync (protocol v4)
- Weather = 18: host sends every float/int/bool property of `WeatherManager.CaptureWeatherSnapshot()` (+ forecast) by reflection, sorted by name, every 5 s and on PlayerJoined. Client: `ApplyWeatherSnapshot` + `RestoreForecast`; prefixes block `RandomizeWeather`/`RollMoonPhase` on guests.
- Tutorial save: `manualOverride` true, clock frozen at 8.83. Verified 2026-10-26 with `mod/cmd.ps1 'time 20'` + `'weather rain'` on host: client logged "clock corrected by 11.183 h", clouds/fog matched, screenshots matched. `ApplyWeatherState(...rain 1...)` left rainIntensity 0 on both (rain needs more than that).
- Dev commands: `mod/cmd.ps1 '<cmd>' [-Instances host,client]` → `cmd.request` → Dev/Commands.cs (time, weather rain/clear).

## Gear (protocol v5)
- Gear = 19: per gear object (PlayerCameraSetup/Backpack, LanternHolder, ItemsInRightHand, Tools/*, every UseItem physicalTool/backpackTool) send active, anchor path (Reka_Body_Base_01/... bone or Backpack/... slot), follower path inside the gear, pose relative to anchor, child renderer/light on-bits. Checked at 4 Hz, sent on coarse change, host replays to joiners.
- Puppet clones the receiver's own copy of that gear object, strips to meshes+lights, reparents the following transform onto the puppet bone. Verified with photos from all sides: backpack and lantern in hand on both machines.
- Gear rig facts: animator layers Base/RightHand/LeftHand (weights now in PlayerSnapshot); nothing parented to bones, ObjectFollower everywhere; lantern mesh is skinned, its rig Bone follows hand.l; unused gear is parked at (0,50,0) instead of disabled.
- Footsteps: injected PuppetAnimEvents.OnFootFall(int) on the puppet animator GO receives clip events (verified: 37 steps in ~15 s of bot walking) and plays WalkingSounds.PlaySound at the puppet foot. Not yet verified by ear.

## World transfer (protocol v6)
- World = 20: on join (deferred until host FileIO is Loaded) host sends its current save folder (minus Thumbnail) gzipped; guest wipes its sandbox saves, writes, FileIO.Rescan(), loads from the main menu. Verified byte-identical, client with no save loaded into the host world at the same spot.
- LiteNetLib PutBytesWithLength is ushort-length (64 KB cap): use int length + Put(bytes).
- Guests: always sandboxed (CoopSaves/<instance> or CoopSaves/guest); SaveCurrentSaveGame/DoSpecialSave blocked by Harmony prefix.

## Post-tutorial test save (2026-10-26)
- Made via dev commands on host: `skiptut` (Cheats.SkipTo=1, RitualIdentifier.ritualFinished=true, SaveLoadGame.SkipTutorialAndJumpToFirstMap) → Leshy trial on a generated map, starting items unlocked. Then `click OK` to clear location popups (save refused while PopUpManager.isShowingPopupOrInQueue), `hutfix` (DeactivateHomeForTutorial.ReactivateThisGameObject + activate ChickenLegsBehaviour + teleport), `save`. On reload a feather-colour prompt appears: `click 2 Brown`, save again → slot 3. Backups in local/backup/host-leshy-save-2/-3 and host-tutorial-save-*.
- Dev commands now: rig, gear, pshot <side>, walk x y s, time, weather, skiptut, status, unblock [step], click <label>, hutfix, unlockall, save, debugsave.

## Hut (protocol v8)
- Hut = Home/mesh_HousePlatform_01 (ChickenLegsBehaviour + Animator: 3 float, 3 bool params, 1 layer, root motion). Floor = Home/PlatformFollower (ObjectFollower -> root.x bone) = PlayerHutBoardDetector.HutPlatform.
- HutState = 21 (host, 20 Hz Sequenced): root pose, isStanding, animator params + state hash/weight. Guest: Harmony prefixes skip AI Update/LateUpdate/OnAnimatorMove/ApplyMovement and ChickenLegsBehaviour TickMovement/DetermineMoveParameter/Rotate/MoveTowardsTarget/EvaluateStandingState/TickLookAtTakeover/TickCaptureHold while mirroring; applyRootMotion off; 100 ms interpolation. HutRequest = 22: guest TryCallTo -> host.
- Riders: PlayerSnapshot.OnHut when PlayerHutBoardDetector.PlayerAboard; position/yaw relative to the floor. Verified: client boarded mirror, host called hut 15 m; client stayed at floor-local (0.35,0.96,-4.01) (PlayerPlatformCarry works on the mirror) and host's puppet showed exactly that floor-local position.
- Not yet: guest steering, map travel gating. (Feather colour: StationSync.)
- Sit/stand ground effects (v28): probed with postfixes. Host on sit: `HideSitCandidatesNow`, `EnvironmentSittingChanges(true)`, then from its own update `ApplySitEnvironmentChanges(gen, "failsafe timer")`; on stand: `ShowHiddenProps`, `EnvironmentSittingChanges(false)`. A mirrored guest hut ran none of them. Now HutState carries the host's last `EnvironmentSittingChanges` value; the guest calls it on change (+ `ApplySitEnvironmentChanges(BeginSitEnvironmentChange(), ...)` after a sit). Verified by the same probe on the guest.

## Inventories (protocol v9)
- Inventory = 23. Guest after load sends Hello(key = Steam ID, + "-<instance>" for dev copies); host replies Assign from `CoopSaves/<host>/CoopPlayers/<key>.inv` (empty for a new player); guest applies via InventoryManager.ISaveable_RestoreSavedState with a patched SaveGame, then sends Update every 2 s on change; host stores the file. Verified: client got its own empty pack (not host's 45 wood), 7 wood survived a restart.
- Not yet: tie .inv to a world identity (it is per host install, not per save); money/use-item unlocks per player.

## Shared pack (protocol v39)
- Replaces the per-player `.inv` files: one pack for everyone, the host's own inventory, saved in the host's save.
- Inventory = 23, kinds Hello (guest key; host kicks a stale connection with the same key and replies Full) / Full (host -> guest: last applied seq + whole pack, every 3 s) / Delta (seq, changes). A Pack is items (collected, crafted, excess by Item id), building-block instances and plans, money (`MoneyManager.currentMoney`; the save's inventory state has none) and wearables.
- Each tick a player captures its pack, diffs it against the last base and sends the delta (guest numbers its own). On a delta: flush own changes first, apply (AddItemsToPlayerInventory / Take from grids then excess; MoneyManager.AddMoney/RemoveMoney; PlayerBBCollection.AddAsInstance / RemoveInstance / AddAsPlan; AddWearable), take a new base. The host relays. A guest corrects drift from Full only when the host has applied all its deltas.
- Perf: capture reads `inventory_collected`/`inventory_crafted`/`excessInventory` directly with cached ids (GetStateForSaving + FindObjectsOfTypeAll cost up to 45 ms).
- Rewards (Reward = 27, now unused) travel in the pack. Guests still skip their own GiveTrialReward and non-world POI_Loot; a POI_Loot under a `POI_Loot*` ancestor (bags in trees, wells) is world loot and anyone can open it.

## Ravens (protocol v40)
- Birds = 38: every 0.1 s each player sends its `UseItem_Birds.spawnedBirds` that are visible (slot, position, pitch/yaw, `birdVisibility` as a byte); one empty message after the last. Host relays.
- Copies: `collectProjectilePrefab` instantiated with CollectProjectile destroyed, rigidbodies kinematic, colliders off, VFX played; materials' `Visibility` set from the stream. Lerped toward the latest sample. Dev: `shoot`, `birds`, `birdtest`.

## Conversations (protocol v41)
- Guest prefixes on what starts a talk or moves the story: `NPC_DialogueManager.SaySomething` / `PresentsDialogueOptions()`, `StandaloneDialogue.StartDialogue` (not passive ones), `TutorialNPCLogic.TriggerInteraction`, `POI_Dialogue.TriggerInteraction(ThanDisableDialogue)`, `GenericPOIFunctions.TakeItemFromPlayer` (Lutki berries). They don't run on the guest; Dialogue kind Talk (act, scene path with sibling indices) has the host call the same method on its copy (ignored mid-conversation). Not forwarded while applying the host's state (`WorldObjects.ApplyingRemote`).
- These are reached from `PlayerDetection.OnTryToInteract` UnityEvents (dev `detections` lists them, `interact` fires one). Other listeners on the same trigger (sitting, sounds, carrying) still run locally.
- Leader: the guest whose request came within 3 s before the conversation began, else the host. Line and Options carry the leader. Host's `DialogueOptionsPanel.OnOptionPicked` is refused unless the leader is the host or the pick came from the leader (Pick, -1 = left the panel -> `CloseAndEndDialogue`).
- Continue: prefix on `DialogueUILogic.WaitForPlayerInputThenDoSth(action)` keeps the pending action and its (generation, index). A guest's Next invokes it (QueueNextLine directly would skip `RunCompletionThenBranch` -> `CallOnFinished` -> `RepresentsDialogueOptions`, ending the talk instead of going back to the options).
- Camera: postfix on `DialogueCamera.Activate(head, name, family, listener)` sends the head's scene path. A guest within 35 m shows lines non-passive and calls `Activate` on its copy of the head right before each line: the camera only takes a speaker for a line that starts in the same frame (Activate earlier -> OnDialogueLineStarted without Commit, never live). Passive lines and a closed box release it ("foreign dialogue on screen", "dialogue UI idle"); an open options panel holds it, so pulled-in guests who aren't choosing get the panel read-only.
- Dev: `talkers`, `say`, `talknpc <path part>`, `next` (host: the pending continue), `pick`, `closeoptions`, `standalone`, `takeitem`, `camstate`, `comps`.
- State (protocol v43): the host sends (phase Idle/Line/Options, leader, line gen+index) every second and on change. A guest showing anything of the host's talk while the host has been Idle for 1.5 s closes it (a lost End); a guest leader whose panel isn't up while the host waits in Options gets it again (2 s after its own pick).
- Next carries the line (gen, index). The host ignores one for a line it has moved past, and holds one that arrives while the line is still revealing (no `_waiting` yet) until `WaitForPlayerInputThenDoSth` hands over the action. Before, it was dropped and both sides waited.
- Leader patience: a guest leader who hasn't picked within 45 s of the options showing hands the choice to the host (Options re-sent with the host as leader).
- Host bystander: a guest-led talk more than 35 m from the host (requested target or the camera's head) doesn't hold the host. The dialogue box asks `CurrentGameMode.RequestInGameUI(DialogueUILogic)` (ScriptableObject, `Resources.FindObjectsOfTypeAll`), and InGameUI mode is what stops walking (`PlayerMovement_2021.blockMovement` stays false). The host de-requests it and refuses new ones, and skips `DialogueCamera.Activate` / `Commit` / `RetryPendingSolve` / `OnDialogueLineStarted` / `LateUpdate` (the camera re-goes live from its own retry otherwise); the box stays up as subtitles. Leaving the state (coming near, the end, or having to choose) re-requests or deactivates.
- The host ignores a Talk for an object that is off on its copy (a once-only scene that already ran).
- Dev: `dlgstuck` (guest: a leftover line), `unblock_move [mode|camera|flag|passive]` (experiments).

## Conversations, one for everyone (protocol v46, 0.3.12)
- Replaces the pull-in range, the 45 s handoff and the host bystander. The starter (whoever's request began it, else the host) steers; everyone is in it.
- Gather: at the first line or options the host sends each other player a spot in a row beside the starter (1.6 m apart, right side first, facing the speaker), grounded by `RaycastAll` picking the hit nearest the starter's height. Not behind: the dialogue camera looks over the starter's shoulder. Nobody is moved back afterwards.
- Camera (`TalkCamera`): the starter sends `Camera.main` pose + fov at 30 Hz (Sequenced; a guest's is relayed by the host). Everyone else disables `Cinemachine.CinemachineBrain` and puts the pose on the main camera in `Application.onBeforeRender`: the game's camera scripts still move it after LateUpdate, so setting it there only showed the local dialogue shot. Released 0.6 s after the last pose. A guest counts as in the talk on a line or on the options panel (options-only talks had no camera before).
- Input: `WaitForPlayerInputThenDoSth` gets a no-op action on non-starters (the host keeps the real one for the starter's Next), and `TalkInputBlock` makes Rewired's `GetButton*` read false for AdvanceDialogue, UISubmit, UIAltSubmit, Interact and Action1 while watching: the box reads the button itself, so a non-starter's press ended the talk before.
- Skip: postfix on Febucci `TypewriterComponent.SkipTypewriter` on the starter sends (line gen, index); others call `talker.typewriter.SkipTypewriter()` once that line is revealing. Hover: the starter's `EventSystem` selection among `spawnedRows` is sent on change; others select that row and `MoveHighlightToCurrentSelection`.
- Dev: `cams` (every camera's pose and brain), `dlgprobe` includes the camera state.

## Puppet looks
- A puppet is a copy of the local body, and the copy's `CharacterAssembler` dressed it again in the local player's look when it activated, sometimes after the remote look had been built (the guest showed up in the host's clothes). The copy's `generateRandomOnAwake` and `initializeFromGenerationDefinition` are turned off before activation, and `OnCharacterGenerated` re-applies the wanted look after any build the mod didn't start (it still fires once per join). Each player also resends their look every 20 s. Dev: `lookcap`.

## Story objects on and off (protocol v42)
- `ActiveSync`: the host indexes every object under the story's scene roots (roots with >= 100 transforms and a unique name, minus Main Canvas, PlayerCameraSetup, UseItems, Scripts, Home, ...): ~14k on the tutorial map, 400 per frame. Characters (GeneralAIController, Animator, Canvas) count as one object; their insides aren't watched. Polls `activeSelf` 400 a frame, sends changes batched 4x a second, and every change since its world loaded to a guest's Ready. Key = root name + /child[sibling index]... Guests `SetActive` under `WorldObjects.ApplyingRemote` (a scene that starts on enable doesn't get forwarded back).
- Verified: hiding and showing a crow (`setactive NPC_Crow_05 0|1`) on the host does the same on the guest. Noise at rest: villagers' held props (HelperRightPropFollower/Hammer, Motyka) now and then.
- Dev: `active`, `setactive <name> <0|1>`, `where <name part>` (where.txt: path, position, visible renderers), `roots`.

## Saved progress components (protocol v42)
- The game's save is ISaveables found by `SaveableSceneWalk` (PushRoots per scene + DontDestroyOnLoad, Step, FillLists; 276 found, each 4x, deduped). `SaveableSync` keeps the ones holding shared progress: every UseItem_* (tool/skill unlocks: the ravens are `UseItem_Birds` / id PlayerBirds), TutorialManager_v2, Tutorial_DisableBabaDuringTut, DeactivateHomeForTutorial, PlayerProgress, OvenUnlocker, BuildingQuestStarter, BuildingRuleHints, CollectionRewardsManager, SaveLoadCollection, BB_Building_NPCComments, PlayerGuidance, PoiMapHistoryTracker. Left out: inventory/money (pack), TasksTracker (trials), static flags (story), house pieces, character, movement, play time, stats.
- Each is captured alone into an empty SaveGame (`saveableProgress`, guidance, collection, POI history serialized with `ClassicElementStateBridge`); the host checks one a frame, each every 3 s, and sends a changed one (gzip); guests `RestoreSavedState` on their copy. Everything on a guest's Ready.
- Verified: `unlockall` on the host unlocks every tool on the guest's wheel (PlayerBirds included). Before, guests never got story unlocks (the playtest's missing raven fetch).
- Dev: `saveables`, `statedump` (every section to statedump/*.xml, for diffing host and guest; right after a join they match except play time).

## Desync detector (protocol v44)
- `DigestSync`: every 15 s host and guest each hash story flags, trials, saved progress components (each one's capture hashed), story objects on/off (villagers' held tools left out) and the pack; the host sends its hashes. A category that differs two rounds running (so changes in flight don't count) asks the host for its whole key/value list; the guest logs every difference and appends it to `desync.log` in the game folder. Hashes are order-free sums of per-entry FNV, read 1.5 ms a frame (~2 ms worst tick). Dev: `digest` (per-category result and cost).
- Payloads over 64 KB need an int length: LiteNetLib's PutBytesWithLength caps at a ushort (the story-object list arrived cut short).
- Found with it: the tutorial's talking crows (TutorialProps .../Birds+PathToBabaJaga, GeneralAIController + VillagerBehaviour) had no place element id, so AiSync/MirrorSync never keyed them and guests' crows ran their own AI; AI outside a place is now keyed by `@` + scene path. A guest also lost story-object changes that arrived before its index was built; they are kept and applied after.
- Local new-campaign run (2026-10-29): the intro's input tutorial can be driven with `fields TutorialManager_v2 invoke SolveCurrentTask` (synthetic `walk` input doesn't count), `interact PlayerDetection all` for the lantern, `click CLAIM`, then walk out and `click Continue`. Guest joined after the first save; all five categories matched through all 19 prologue tasks (`skiptask`, which ticks tasks without running their scenes). Dev: `fields <Type> [invoke <method>]`, `saveablexml <type part>`.

- Self-repair (0.3.13): after logging a detail list, the guest takes the host's values for "active" (`ActiveSync.MatchHost`, through ApplyAll) and "trials" (`TrialSync.MatchHost`, through Apply). Seen in a run: a guest-only tick of trial 0 task 0 (host 0, here 1) that the host's diffs never corrected, since the host only sends its own changes. Verified both by forcing a guest drift (`skiptask`, `setactive`): logged, switched, "matches the host again" one round later.
- Trial 0's tasks are `TasksTracker.<InitialiseTasks>b__108_N` predicates; with 10 mushrooms in the pack both copies read false before the trial's intro (`introPlayedTrialIDs` empty, `MapIntroDialogue`), so the guest's tick came from somewhere else. Dev `taskfn` prints each task's predicate, its result and its lambda.

## Quest progress both ways (protocol v47, 0.3.14)
- From the 2026-10-29 from-scratch playtest: a guest's own story actions (the talisman, the mortar-and-pestle charm, "collect 10 mushrooms") stayed on the guest, and the 0.3.13 self-repair then reverted them. Everything synced host -> guest only.
- Now each table also goes guest -> host and the host takes it on; the host's normal sync brings it back to everyone:
  - Story flags (`StorySync`): the guest diffs its four tables against the host's last applied set once a second and sends its own changes as Flags; the host applies them with the game's setters.
  - Tool and skill unlocks (`SaveableSync`): `UseItem_*` components only. The guest compares its capture with what it last applied from the host (stored as read back after restore, so a round trip doesn't echo) and sends a change; the host restores it. Other components (tutorial, progress lists) stay host-only: whole lists the host may be ahead in.
  - Trials (`TrialSync` Gain): task done (`T{i}.{j}`), quest steps, `tut.*` counters, only upward; the host applies where it is behind and evaluates.
  - Stats (`TrialSync` Stats): tasks count the player's own `StatsTracker` (pickups per item, crafts, places visited/solved, people/ghosts/creatures helped, teleports...), not the shared pack. The guest sends positive deltas; the host adds them to its own and evaluates, so the host's tasks count everyone.
- The self-repair for trials now only fixes where the host is ahead; a guest's gain the host hasn't taken on yet is left alone.

## Hut driving, mirror, names (protocol v49, 0.3.14)
- Only the host drives (user decision 2026-10-29). `ChickenLegsBehaviour.StartControlling` is refused on a guest's mirror, and on the host when `DialogueSync.RecentLeader` is a guest (the hut's "Hut, Hut, go where I say!" runs on the host for a guest-led talk). That guest gets HutRequest `Refused` and sees "Only the host can drive the hut." once the talk is over (the talk's end closes the dialogue box, so it waits). Guest steering code is dev only now.
- HutState carries `Driving` (host at the controls). Guests then watch: TalkCamera streams the host's camera as in a conversation, TalkInputBlock applies, and `PlayerMovement_2021.BlockMovement(true)` holds the guest still (`UnblockMovement()` after). Verified locally: same view on both, guest pos unchanged while holding W, walks again after.
- Stray box after a talk (issue 12): for 3 s after the host's End, and when the host starts driving, a dialogue still showing the last line the mod put up is closed. Not reproduced locally; with the host-led hut talk the guest showed no box.
- Mirror (issue 13): a talk under an object named `CharacterCustomization` (the `BB_cc_mirror_1` block; there's no component of that type on it) is personal: a guest's runs on its own copy, not sent to the host; the host's isn't shared or gathered. Verified: guest mirror talk, host log silent.
- Names: each player sends Steam name + instance name; the tag shows the Steam name unless two players share it (two copies on one account), then the instance name. `-coopinstance olga` showed "olga" before.
- Test with a copy of Olga's world: `game/play/CoopSaves/play/- 1 -` (the dev world is in `local/backup/play-world-before-olga-copy-2026-10-29`). Dev: `fields ChickenLegsBehaviour invoke StartControlling|StopControlling`, `fields PopUpManager invoke ForceCloseAll`.

## Player detectors and talks started by NPC scripts (protocol v49)
- `ObjectDetector` with tag "Player" (story triggers: the old woman's `PlayerDetector`, monologues, the meet-Baba weather, ritual candles, the Lutki) fire per copy for that copy's player. `DetectorSync`: on the host, a polling detector the host isn't inside is ticked with a guest's position when that guest is inside (`ContainsPoint`; the manager passes the head, so the puppet's feet + the host's head offset); `OnEntered`/`OnExited` don't run on guests. A host enter caused by a guest holds `DialogueSync.NoteRequest(guest, 10 s)` so a talk it sets off is led by them. Animals' flee triggers stay FleeSync's. The manager ticks all registered detectors (73 in the tutorial), wherever the host is.
- The old woman's door talk ("Fu, fu! ...") starts from her AI calling `DialogueFunctions.StartDefaultDialogue(string, Action, bool, Nullable<DialogueBranch>, bool, bool, string[], Action)` (two overloads; the 8-arg one is patched), right after the guest claims the talisman. On a guest that call now goes to the host (Act 9, key + flags + string[]; the callbacks stay behind) and runs there led by the guest. Verified from the pre-lutki save: host far away, talk runs on the host, host brought beside the guest, the guest steps through all 5 lines, ends on both.
- Verified with the same run: Lutki planks (4 conditions on both, the Lutki stands, "Take talisman" on both), the guest's talisman (backport unlock and stat taken on by the host), Reka's Lutki lines on the guest, the meet-Baba weather on both.
- Not done: the guest taking the book (`CollectibleJournal.OnCollect`, "Go to the bench and take the book") is put back by the host's place sync ("changed here by itself; asked the host for its copy"); same family as the talisman before 0.3.14. The gate (issue 7) wasn't reached.
- Dev: `[dlgstart]` logs each dialogue the game starts and the talk calls just before it (`TalkCallLog`; the game's own stack isn't readable), `detectors [r]`, `buyables`. Scratchpad `lutki-setup.sh [door]` drives the pre-lutki run (needs `local/saves/pre-lutki-2026-10-29` copied into `game/play/CoopSaves/play/- 1 -`).

## Playtest fixes (protocol v48, 0.3.14)
- Guest hand-ins: the host switches on a talk a guest asks for when it's off on the host (the wheel's `Dialogue_FoundWheel` turns on with the carrier's own pickup), unless it's a once-only StandaloneDialogue that already ran.
- ActiveSync skips the insides of CarriableObjects (colliders and hint effects switch with who holds them); the self-repair turned the host's wheel `AxelCollider` on inside the guest's arms and shoved the guest 110 m. A locally carried object switched off by the story is let go.
- Drops: the dropper sends Carry `Roll` (pose, resting) at 10 Hz until the object sleeps or is still for 0.5 s (10 s cap); the others hold it kinematic, follow, then restore its physics. Verified both ways: identical resting point.
- `ConditionSync` (MsgType 43): GenericPOICondition SetConditionTrue/False and Tutorial_FenceGateLock Close/Open on any copy go to all (host relays). The Lutki's four planks each set a condition on the picker's copy only. On a gate close the others move in beside the closer.
- `PopupSync` (MsgType 42): PopUp_Loot.QueuePopUp is shown on every copy (items come through the pack). POI_Decision.TriggerDecision on the host goes to the recent conversation leader (`DialogueSync.RecentLeader`, 5 s after the end); others see it read-only; the leader's pick runs OnOption1/2 and OnAnyOptionChosen on the host.
- Passive lines on the host (Reka's monologues) are sent to guests as Dialogue `Passive`.
- Baba Jaga: `BabaYagaStartTalking.StartTalking` is forwarded like other talks (Act 8). Unverified; `BabaJagaEncounter.StartEncounter(Transform)` and `BabaHouseTrigger` are left alone.
- Dev: `walkto x z [s]` (DebugMoveInputOverride is (forward, right) relative to the camera's yaw), `drop`, `pos`, `[cond]` log of every condition set; scratchpad tool `finder` (System.Reflection.Metadata over the interop dll) lists Type.Method by name.

## Carrying story props without an id (0.3.13)
- `Merchant_Cart_Wheel_Tutorial` (Tut_Village, POI_TutorialMerchant_LostWheel) has no UniquePoiElementID: "Carry: ... has no id; not shared", so the host never saw the guest holding it. A CarriableObject with no element id and no AI is now keyed by `path:` + its ScenePath, cached per object before the first pickup moves it. Verified: guest picks the wheel, host shows player 1 holding it.
- The delivery: `Dialogue_01/01/Dialogue_FoundWheel` (off) has a GenericPOICondition fed by other objects (`objectsInfluencingThis`); `Merchant_Cart_Wheel_Tutorial_Attached` (off) is the wheel on the cart. Not driven to the end locally (needs real walking with the wheel; dev `tp` while carrying left the wheel behind). Check in the playtest: a guest returning the wheel, and whether the host's copy shows it on the cart.
- Dev: `tp x z [y]` now ignores trigger volumes (it landed on a volume ~22 m over the village); `fields Type@name` picks the object by name.

## Game build 26298851 (hotfix 1.0.25, 2026-10-29)
- Any IL2CPP type injection crashed the game in coreclr (0xc0000005, same offset every time) a few seconds after Steam init, with or without our plugin (BepInEx's Unity log listener injects a delegate type). Il2CppInterop's byte signature for `Class::GetDefaultFieldValue` matched an unrelated game function (0x...77BFC650, ~4.7 MB below the runtime's other functions), which it then detoured. `Il2CppFixes` makes `Class_GetFieldDefaultValue_Hook.FindTargetMethod` use its own fallback (walking from the export `il2cpp_field_static_get_value`), before our first `ClassInjector` use. BepInEx's listener injects before plugins load, so the package also ships `BepInEx/config/BepInEx.cfg` with `UnityLogListening = false` (BepInEx fills in the rest). All dev copies have it set too. Found by bisecting: no plugin + listener off ran; registering one injected type crashed.
- Guest join landed in the new-game intro once: `TryGetMostRecentSave` named the guest's old slot (`$AUTOSAVE$1`, a scan from before the write), the write had removed that folder, and the game's "LoadFullSaveGameData failed ... Going to tutorial" started a new game. Before loading, the guest now moves the host's files to the folder of whatever slot the game picked.
- After both: local join into the host's world, all five digest categories match, villagers and crows in the same places, no warnings, frames avg 17 ms.
- Interop regenerates on the first start after a patch (Cpp2IL ~15 s plus generation); relink the copies with `mod/sync-game.ps1` first.

## World pickups and destruction (protocol v10-11)
- No running numbers on this map's collectibles (names lack "_Collectible_N"), but all 1822 CollectibleIDs match in type + position on both copies. Key = Item name + position, matched within 0.5 m.
- WorldObject = 24: Collected(key) both ways (host relays), Ready/GoneList for late joiners (session set). Hooks: CollectibleID.OnCollect/CollectItem/TryCollectItem, UseItem_Birds.CollectItem (partly inlined; deduped per instance). Remote removal = CollectibleID.DestroyCollectibleExternal (no items granted).
- Verified both directions; host save after a guest pickup keeps it gone after reload, exactly like a local pickup (berries/mushrooms/honeycomb respawn on reload even locally - game behaviour). Guest shows restored gone state a few seconds after load.
- Destructibles (protocol v11): 4237 DestructibleObjects (4172 trees) match by name + position on both. Damage(key, amount) is sent after a DamageObject that landed and replayed with DamageObject on the others; pieces of a destroyed object scatter randomly, so they are keyed "D:<name>#<index in collectibles>" (cached at destruction, since the game unlinks a piece when it is taken). History is replayed in order to late joiners; an unmatched change is dropped after 15 s so it can't stall the queue.
- Trees need UseItem_Birds.canDamageTrees (static, set by the story; dev command woodcut). Felled env trees come back on reload even for the host's own felling - game behaviour.
- Dev: coll, collect [type], hit [dmg] [tree], woodcut; scratchpad hostsave.sh clears popups and saves.
- Not yet (first-come arbitration since done, v26): chests/POI loot, containers, crafting, LeshyApproval felled-tree count for guest fellings (host counts them because DamageObject runs there - unverified).

## Building (protocol v12-13)
- Build = 25. Each machine polls its placed set (0.3 s in build mode, 1 s otherwise) and diffs against the last agreed one: house blocks by placementIndex (HouseBlockSpec.FromPlaced relative to BB_Building.Home + type id + cut/fit settings), furniture by type + hut-local position (cm). Changes go to the host, which applies, relays, and sends a full Snapshot to a guest once both houses have settled (guest diffs and applies).
- Apply = game load path, no cost/undo/refund: blocks SpawnHouseBlockInHome(spec, Pose(refPos, refRot), index) / RemoveHouseBlockUnrecorded; furniture InstantiateBB + FinishPlacedBB (SpawnBB returns null outside build mode) / UnregisterRemovedBB + TearDownRemovedBB.
- Verified: blocks placed (TryCommitSpec, paid from the builder's own pack) and removed (RemoveBBFromWorld) on either side show on the other, house dumps identical; guest-built block survives host save + reload on both. Furniture place/remove both ways.
- Furniture parent paths contain per-machine instance ids (houseSubPart_NNNN): matched as deep as names agree, positions are absolute (hut-relative) anyway.
- Not yet: chest/station contents, blueprint sites, PlayerBBCollection placed counters for blocks built by others, chapter-locked removals, cut-tool edits (should be covered by the diff, untested).
- Index lanes (0.3.4): `HouseBBPlacement.AssignPlacementIndex` (static `s_lastPlacementIndex`) gives a new block the next index; a postfix bumps it to the player's lane (index % 8 == player id % 8) and moves `s_lastPlacementIndex` along. Remote spawns (`SpawnHouseBlockInHome` with an index) are left alone. Without lanes, 200 fuzzed concurrent edits diverged by a block: the host's renumbering of a collision could land on a guest's newer block, which the guest then overwrote unseen. With lanes: 200 and 300 edits ended identical (hashes match) with no renumbering. Indices grow ~5 per placement.
- Dev: house (house.txt dump + hash), houseroundtrip, buildnext <index>, unbuild <index>, furnish <typeId>, unfurnish <typeId prefix>, `buildfuzz <n> <seed> [base]` (n random builds/removals 0.1-0.2 s apart; removes only blocks above base), `unbuildabove <index>`.

## Story (protocol v14-16)
- Dialogue = 26 (host -> guests): polls DialogueUILogic (IsTalking, !currentDialogueIsPassive, DialogueGeneration + indexInDialogue) and sends each line (name, title, text); postfix DialogueOptionsPanel.Show sends the option labels, prefix OnOptionPicked the pick. Guests show them as passive lines via DialogueUILogic.RequestStartDialogue(..., isPassive true) - subtitle bar, no camera/input lock. Verified with Baba Jaga's real options + 4-line answer, and a screenshot of the guest's subtitle.
- Host pace (0.3.6): passive requests queue behind a passive line on screen (`DialogueUILogic.queue`) and each holds for `delayForPassiveDialogue` (2 s), so a host clicking fast left guests far behind. Guest now clears `queue`, `ForceCloseDialogue()`s its passive line and starts the new one; `delayForPassiveDialogue` is 3600 while following, restored on End. Options show "..." under the host's name, the pick as a line by the host. Host sends End only after 0.5 s with neither a line nor the options panel up (the panel hands over across a frame or two), and a pick counts as talking so an option that ends the talk still closes guests' line.
- Reward = 27: around TasksTracker.GiveTrialReward and POI_Loot.GiveItemsToPlayer the host diffs its items/money, PlayerBBCollection (instances, plans) and wearables; the gain goes to guests, who take it via LootTable.TryAwardLoot (popup) / AddAsPlan. Guests skip their own GiveTrialReward / non-world POI_Loot. World loot (TreasureChest, Lootroom, DestructibleObject parents, names with Destruct/LootRoom/Chest, or loot dropped while DamageObject runs) is first come; a pot broken remotely gives the host nothing. Verified: POI_FillMyPantry small reward copied (2 BBs). Trial 0/1 rewards gave nothing on this save.
- Story = 28: StaticGameCondition / StaticGameValue / StaticMapCondition / SaveLoad_HeardDialoguePerMap tables, host -> guests: all on guest Ready, then changes polled at 1 Hz. Applied through SetCondition / ChangeCondition / SetHeard (values: dict + onChanged). Verified both.
- Not yet: TasksTracker trial/chapter progress mirror (guest's own Evaluate may still solve trials locally), POI element states (CompletableStation, decisions), NPC positions, blocking guests from starting their own conversations, guest's map following the host's travel.

## Following the host to a new map (protocol v17)
- World = 20 now has a kind byte: Save (as before) / Travelling. Host prefix MapChanger.JumpToNewMap/JumpToSameMap/JumpToMap -> Travelling to guests; then it watches the save folder and sends the first save written after the trip (the game's own arrival autosave, $AUTOSAVE$N) once it has been quiet 3 s. (Postfix on SaveLoadGame.OnGameSaved never fired.)
- Guest: on Travelling, stops inventory reports and shows a subtitle. Receiving a save while playing: write, Rescan, wait for FileIO Loaded, then SaveLoadGame.LoadSaveGame(folder, slot) in game. Guests' own MapChanger jumps are skipped except during the 120 s after loading the host's world (the load itself jumps).
- Per-world sync (inventory, world objects, building, story) restarts on MapChanger.FinishGenJump / FinishHandPlacedJump / SaveLoadGame.ApplyLoadedSaveGame, since the player object survives a map change.
- Bug found and fixed: during the guest's in-game reload its pack briefly held the host's items and was reported as the guest's. Now the guest neither asks for nor reports its inventory while SaveLoadGame.isLoading (+3 s); host keeps <key>.inv.bak. (SaveLoadGame.worldIsLoading stays true on guests indefinitely - unusable.)
- Verified: host travel (dev command travel -> JumpToNewMap) to Barzlin; guest follows into the same map at the same spot, gets house snapshot + flags, and keeps its own 3 wood.
- Host-side trip needs the chapter card / location popups dismissed before the arrival autosave is written (a real player clicks them; dev: unblock step + click OK).

- Loading a save on a generated map runs `MapChanger.JumpToNewMap` again after `ApplyLoadedSaveGame` (with `worldIsLoading` true): it's regeneration, not travel. The host ignores jumps while `worldIsLoading`; a guest allows them for 120 s after it starts loading the host's save (menu or in-game).
- Guests' NPC conversations run on the host since protocol v41 (see Conversations); before, they were blocked with a subtitle.

## Containers (protocol v18)
- `InteractableInventoryLogic` covers chests, ovens, displays. Contents via `GetSerializedState()` / `RestoreSerializedState()` + `UpdateUI()`.
- Key: hut-local position (cm) for containers under `BB_Building.Home`, world position (dm) otherwise. Polled 2 Hz; whole contents sent on change, last write wins; host relays and snapshots on guest Ready. Unknown keys wait 20 s (map streaming).
- Verified: guest puts wood in the bread oven, host has it, survives host save and restart on both.
- Some containers store one item per stack (oven input shows 1+1 on the host after applying 2).

## Trials (protocol v19)
- `TasksTracker`: `_currentTrialIndex`, `trials[i]` (`hasBeenFinished`, `_isComplete`, `suspended`, `tasks[j]._isComplete`), static `completedSteps` (QuestStepID), `tutorial_*` fields, `claimedRewards`.
- Host flattens these into named ints, 1 Hz diffs, all on Ready. Guest applies (`SetTrial` for the index, `OverrideAsCompleted` for tasks, reflection for tutorial fields) then `Evaluate()` to refresh the quest display.
- Guests can't `AdvanceToNextTrial(bool)`, `SolveTrial`, `SkipCurrentTrial` on their own.
- Verified with dev `skiptask` on the host: task ticks and trial completion arrive on the guest.

## Hut controls (protocol v35)
- `ChickenLegsBehaviour.StartControlling()` (camera director, `isBeingControlled`, `ControlMode.PlayerControl`); the player stays where they are, the hut walks. `DetermineMoveParameter` reads input into `moveAxis` (x forward/back from W/S, y turn from A/D, smoothed) and `oldMoveAxisRaw`; `TickMovement` moves.
- Guest steering: the mirror runs `DetermineMoveParameter` while its hut `isBeingControlled`, and sends `moveAxis` + sprint at 20 Hz (HutRequest Steer, Sequenced). The host sets `isBeingControlled`, `SetControlMode(PlayerControl)` and overwrites `moveAxis` in a postfix. On release (or 1 s of silence, or the guest leaving), mode `None` (AI control would walk it back to its old target).
- Vanilla entry: the look-at takeover (`TickLookAtTakeover`, `LookAtTakeover` mode, `OnLookAtPromptPressed`) engages on the host near the hut, but no prompt or key works on the test save; probably locked by story progress. On a guest mirror `TickLookAtTakeover` doesn't engage even when called. The chair `HutControlPlayerSittingPlace` (BB_Chicken_Skeleton_Chair) sits the player but doesn't start control. Input actions: Interact=E, Backport=H, AutoWalk=Q, SitDown unbound.
- `OnStartControlling` / `OnStopControlling` toggle WalkingSound, PlayerIsControllingTheHut / PlayerIsNotControllingTheHot, root.x; not invoked on the host for a guest's steering.
- Riding: a guest aboard while the hut walked 116 m kept the same floor-local position (1.06, 3.94, -3.71) on both machines.
- `ControlMode.PlayerControl` switches off `Home/PlatformFollower/Floor` (layer PlayerHouse): vanilla's driver sits in the seat. With a guest driving, the host's own player standing aboard dropped through to the terrain (16 m) as soon as the mode switched; this was also the "host frozen in mid-air" after dev `hutctl`. The host now records the platform's enabled, non-trigger colliders at steer start and keeps them on every `DetermineMoveParameter` while the guest drives. Mode None keeps the floor but the hut doesn't walk (TickMovement needs PlayerControl).
- Ride exit: guest seated at the controls (dev `seat`, `hutctl`), host standing aboard, ~504 m over two drives (326 m to a river bank where it can neither go on nor turn, then 178 m after an AI call freed it); both platform-local positions held to 0.01 m at every 10 s sample. The hut doesn't turn while blocked (turn input arrives, yaw stays): looks like the game's own rule.
- Dev: `steermode <ControlMode>`, `mestate` (player ground/carry/flags), `rideable`, `tp <x> <z>`. A Harmony tracer over hut/player methods crashed the host (even 16 methods); don't.
- Backport: `UseItem_Backport.ActualPort()` on a guest lands next to the mirrored hut (`GetTeleportPosition` follows the mirror), no mod code needed. Holding H (`Backport` reads as pressed in Rewired) starts nothing on host or guest on the test save, so the hold path is the game's own gating (maybe the item must be equipped); `backport_forbidden` is false. Dev: `backport [go]`.

## Carrying, seen by others (protocol v36-37)
- A watching machine never resolved `CarriableObject.ai` (the carrier's game does in `ResolveCarriedAI` on pick-up), so the animal skipped `HoldCarriedAI` and kept its own pose. Now resolved in `Hold`; its AI is paused and it plays the carrier's animator (carried state 1241552144 on hares), sent with each Pick.
- Carry arms are `LeftArmRig`/`RightArmRig` (TwoBoneIK) at weight 1 while carrying; the player animator doesn't change. While either rig is above 0.01, the snapshot carries local rotations of shoulder/arm_stretch/forearm_stretch/hand on both sides; puppets apply them in `LateUpdate`. `AimRig` stayed at 0 near and talking to villagers.

## Frame hitches and garbage (0.3.4, protocol v37)
- The 35-50 ms stalls every ~30 s were not the game's GC count nor .NET's (neither changed in hitch frames, checked a frame either side), and a pure C# spin never stalled; they sat inside calls into the game, mostly the scene scan.
- Unity's incremental GC (3 ms slices); a full `GC.Collect` takes ~100 ms at a 600-950 MB heap. The game heap grows ~3 MB/s with the mod's ticks off and 6-8 MB/s with them on (dev `alloc start [pause|<tick>]` / `alloc end`, summing per-frame heap growth). Per-tick heap deltas aren't trustworthy: the heap figure updates lazily.
- The scan now fills one reused `Il2CppSystem...List<T>` per type (`GetComponentsInChildren(bool, List<T>)`, `GetComponents(List<T>)`) instead of a new array per root and type. .NET allocations dropped from ~5.5 to ~1 MB/s and the scan's worst tick from 35-50 ms to 2-10 ms.
- Stations near a player (25 m) are captured twice a second, others every 5 s; places within 80 m of a player's position every pass, others every 10 s.
- After: 0 frames over 50 ms in most 30 s windows on both machines (was 1-3). A place's first look records only its parts (the whole capture waits for a change), so the trial site's whole capture (925k chars, ~65 ms) now happens only when its parts change. Each remaining tick still adds ~0.5 MB/s, probably value-type returns boxed by `il2cpp_runtime_invoke` on game (non-icall) methods.

## Guests startle animals (0.3.4)
- Flee triggers are `ObjectDetector`s (tag "Player", polling path, ~5 m sphere) whose `OnEnter` calls `GeneralAIController.GetTargetFromVariousScripts(GameObject)`; `ProximityDetectorManager` ticks them with the host player's position (`_playerT` = "nose"). `PetEligibility.FleeTriggers(ai)` / `IsPlayerFleeTrigger(d)` / `HasLivePlayerFleeTrigger(ai)` find them.
- Host: a prefix on `ObjectDetector.ProximityTick` swaps in the nearest puppet's position for flee triggers when a guest is closer, and a prefix on `GetTargetFromVariousScripts` hands the animal the puppet to run from. Other Player detectors (story, areas) are left to the host's player.
- Wild hares have no live flee trigger (0/10; likely pettable), trapped hares neither; wild deer 13/21 live (a detector switches off for a while after a flee). Verified: a guest 4 m from a deer, host 128 m away, startled it on the host and it ran off on both machines. Dev: `flee [all|count|tp <m> [name] [min m from the other player]]`, `tp <x> <z>`.

## Hut stations and plants (protocol v20)
- House-side saveables (`SavableBBState`, `SavableBBMultiState`, `PlantGrowth`, `IvyGrowth`, `BB_Logic_Workshop` (cauldron/oven queue), `BB_Logic_Mill`, `BB_CandleMakerLogic`, `BB_Logic_RitualDesk`, `BB_Logic_FairyGarden`, `BB_Logic_DeerFeedingPlace`) write into `SaveGame.savedBBs` lists keyed by `gameObjectID` (per-process, differs between machines).
- Capture: `new SaveGame { savedBBs = new SavedBBs() }` → `ISaveable_GetStateForSaving` → `ClassicElementStateBridge.Serialize(typeof SavedBBs)`; ID swapped for a token. Apply: deserialize with the local ID, `loadedObjectsIDLookUp = [(id,id)]`, `ISaveable_RestoreSavedState`.
- Diff ignores `time*` elements, `waterLevel` rounded to 0.25. Keys: type + name + hut-local cm or world dm.
- Wild plants (`BB_Plant_* (Clone)`) are scene roots, not under the hut; covered by world-position keys.
- Verified: plant stage set on either side arrives on the other. The hut's `FeatherColorController` (not under the hut transform, no object ID) is keyed by type alone; feather colour verified both ways.
- `ClassicElementStateBridge.CaptureState/ApplySavedState(poiRoot)` exists for POI element state — candidate for POI sync.

## Places / POIs (protocol v21)
- `POI_SaveLoad` per place, `uniqueID` stable across machines (from the save). Capture: `ISaveableMapElement_GetStateForSaving(new SaveableMap())` + `ClassicElementStateBridge.Serialize`; apply: `Deserialize` + `ISaveableMapElement_RestoreSavedState` (under `WorldObjects.ApplyingRemote`).
- 91 places on Barzlin; all together 147 ms / 1.7 MB XML; `POI_LeshysTrial` alone 52 ms / 920 KB (2068 transforms). Round-robin one place per 0.1 s; places slower than 20 ms re-checked every 30 s. Gzip on the wire (snapshot ~380 KB).
- Signature leaves out: floats, `<x|y|z|w>`, `SerializableTransformActiveWithID` (villagers' tools flicker), `SerializableAIController` (animal/villager AI), `WaitAndRun …pendingRunMs` countdowns, `setActiveOrder`, `debug`. With that, 90 s idle produced only a real change (`RegisterLocationOnCompass` completed), applied on the guest.
- Known: `Property_Merchant` restore threw an NRE once (when noise triggered a re-apply). Villager `SerializableCharacterAssemblyData` changed once on the host after load.
- v24: places also change by themselves on each machine (a request rolls its item, villager looks re-roll after load). A guest only sends changes for places within 40 m of itself; a far one that changed gets `Request` -> the host's copy. Measured: 6 requests in 5 min, each once, no loops.

## Villagers and animals (protocol v23)
- `GeneralAIController`: `_uniquePoiElementID` (stable from the save), `aiBehaviours`, `currentBehaviour`, `SwitchToBehaviourIndex(i)`, `SetAIPos(Vector3, placeOnGround, forceOnNavMesh)`. 48 with IDs on Barzlin.
- Host sends all of them at 2 Hz (12 per unreliable packet; LiteNetLib's unreliable limit is 1023 bytes). Guest keeps its own AI running, switches to the host's behaviour index when different, warps when > 2.5 m off.
- Measured: nearest villagers/animals within ~1 m on both; ~24 warps a minute across 48 AIs (each roughly every 2 min). Smoothing instead of warping is a possible improvement.
- v25: host also sends `targetPos`; guest calls `SetMoveGoal(goal, false)` when its own differs by > 1 m (wanderers otherwise pick their own). Drift 0.4-5 m is walked off at +1.5 m/s toward last pos + velocity * age (`agent.Move` on the navmesh); warp only > 5 m. Nudging alone made it worse (~50 warps/min: donkeys, deer, villagers diverged to different targets); with goals ~6/min, mostly deer/hares fleeing (each machine's own player scares them).

- v32-34 (MirrorSync): near a guest the host streams animals (120 m) and villagers (80 m) at 10 Hz: position, yaw, up to 32 float/bool animator params as halfs, and each layer's `fullPathHash` + `normalizedTime`. Guest copies are pure mirrors (`ai.enabled=false`, agent off, root motion off), 150 ms behind; a copy unheard for 3 s gets its AI back. A still one (same quantized pos/yaw, params, state hashes) is sent once a second. Packets are filled by size (~900 B).
- Animator state: the guest follows a host state change at once (`CrossFade(hash, 0.15, layer, host time)`), but a copy that drifts out of the host's state by itself is only put back after 0.5 s; without that grace, ~90 blends/s in town.
- `AnimatorDistanceThrottle` (also LOD, occlusion and renderer culling) stops animating AIs far from or hidden from the camera. The host sets `skipThisThrottle` + `SetAnimatorLive(true)` on streamed animals and on villagers within 40 m of a guest. On guests, "anim -" in `npcs` is that guest's own occlusion culling.
- Measured in Barzlin town: positions equal when standing and within 0.3 m when walking; ~20 packets/s. With the host 110 m away at the hut, villagers by the guest still move and match.

## Name tags (protocol v22)
- Each player announces Instance.Name (sandbox) or `SteamClient.Name`; host relays and sends the roster to newcomers. Drawn with IMGUI from `CoopDriver.OnGUI` at puppet root + 2.1 m.

## Frame cost
- Scene searches are the cost: `FindFirstObjectByType` ~100x a field read, `FindObjectsByType` similar (bench: 33 ms / 17 ms vs 0.35 ms per field read, in the 2-instance sandbox at ~10 fps, so absolute numbers are inflated).
- `Sync/Find.cs` caches single objects (until destroyed) and lists (max age); `ResetWorld` clears it. Stations look only under the hut except plants/ivy; stations and containers check 2 per frame from a cached list; build polls every 5 s outside build mode.
- `[perf]` log line every 30 s lists modules whose slowest frame was >= 2 ms. After the changes: stations list rebuild 680 -> 89 ms (every 30 s), containers ~25, places ~80 (the big trial POI every 30 s).
- 2026-10-27: places compare by parts only: `poiElementsToSave[i].poiElement.GetStateForSaving(map, uniqueID, id)` + `_elementStates.Capture(map, uniqueID, transform)` gives everything the signature keeps (the meta block is transforms and debug text) at 1.9 ms for the trial site instead of 63 ms; the full capture runs only when the parts changed.
- The loaded world has 12,500 scene roots; `scene.GetRootGameObjects()` as an array cost ~65 ms per scan pass (one managed wrapper each). `GetRootGameObjects(Il2Cpp List)` filled natively and read by index spreads it out; roots with more than 8 children are split into their children.
- Hut stations polled 2 pieces every frame even with 2 pieces in the hut (120 XML serializations/s); now each piece twice a second.
- Remaining: 35-50 ms hitches every ~30 s that land on random small objects in whatever module runs (likely GC); `[perf]` now prints whole-frame average/worst/count over 50 ms.

## Player package (LAN test)
- `mod/package.ps1` -> `local/release/RekaCoop-<ver>.zip`: pinned BepInEx be.791 + publish build (no DEVBUILD code, `DebugType=none` so no local paths) + `README-RekaCoop.txt` (from `mod/README-players.txt`).
- Steam launch options: host `-coophost`, guest `-coopconnect <host IP>` (UDP 27877). Without them the mod is idle.
- Verified: publish DLL as a plain guest (no `-coopinstance`): sandbox `CoopSaves/guest` in the game folder, Steam name tag, empty pack, world loads.
- Host loads a save after a guest joined at the menu: guests that got a different save are sent the loaded one and switch in game (dev `-coopautoloadslot N` reproduces it).

## Same item, same instant (protocol v26)
- Host's order decides. A guest's `Collected` for a key the host already has as collected in the last 10 s (items may grow back later) is not applied or relayed; the host sends `Denied` and the guest calls `InventoryManager.RemoveItemsFromPlayerInventory(item, amount)` from its record of recent pickups (30 s), with a subtitle.
- Verified: `collect` sent to both copies at once, 3 of 3 races: host kept the grain, guest gave back 1; a lone guest pickup still goes through.

## Two blocks placed in the same instant
- New blocks get `BB_Building.HighestPlacementIndex()+1`, so simultaneous placements collide on the index; before, each side replaced the other's block and the houses diverged.
- Host compares an incoming Put with its live house: same index but a different block (`SamePlace`: type + positions) -> the incoming one gets a free index, is relayed under it, and the sender gets both (its block re-indexed + the host's at the old index).
- Verified: `buildnext` on both copies at once -> collision at #75, guest's floor moved to #76, both houses 75 blocks with the same hash.

## Blueprint sites (protocol v27)
- Sites are `SavedBBs.blueprintSites` (`BlueprintSiteData{cardID, framePosition/Rotation, blocks, states, layoutPlacementIndex}`); `BB_Building.GetBlueprintSitesForSaving(ids)` / `ClearBlueprintSites()` + `RestoreBlueprintSites(list, placed HouseBuildingObjects, ids)`. Pieces built into a site are ordinary house blocks (BuildSync).
- Whole list as SavedBBs XML, checked once a second, sent on change, host relays; joiner gets the host's on Ready.
- Test saves have blueprints locked: dev `bpmake` calls `DevForceBlueprintUnlockStage` and restores a one-block site (card `HouseBlueprintCard_DefaultDormer1`). Verified: host's site appears on the guest, a guest's second site appears on the host, no echo. Filling a site in through the UI is untested.

## Guest packs per world (0.3.1)
- Host stores `CoopPlayers/<world folder>/<steamid[-instance]>.inv` under its save root (world = the loaded save's folder, e.g. `- 1 -`). Missing -> falls back to the old `CoopPlayers/<key>.inv` (one pack for all worlds, before 0.3.1). Verified: old pack picked up, first change written per world.

## Tool switching (0.3.1)
- Dev `equip [n]` (`UseItemManager.SetCurrentItem` over unlocked `allItems`), `gearlocal` (what the local capture sends, with every follower's distance).
- Two bugs, both fixed: the lantern's bone has two enabled `ObjectFollower`s at once (backpack hook and hand); the capture took the first, now the one the bone sits at. On the puppet, a follower bone moved onto an anchor could no longer be found by path inside its copy, so the next anchor change failed and the copy stayed where it was; followers are now cached per copy, and a failed placement hides the copy instead of leaving it.
- Verified: lantern -> pine -> nothing -> lantern; lantern hand/hook and pine hand/backpack slot match on both every step.

## Reconnecting
- Guest retries `Connect` every 5 s while it has no host (start in any order; host restart). Stops after a `Reject`. `PlayerLeft(0)` only for a live connection that drops, not per failed retry.
- A reliable message sent right before `NetPeer.Disconnect()` is dropped: the reject reason travels as the disconnect packet's data (`Disconnect(writer)` -> `DisconnectInfo.AdditionalData`).
- Guest connected at the transport but no HelloAck within `HelloTimeout` (15 s) -> drops and retries (was: stuck, since retry needs no peers). SteamTransport doesn't run the delayed `CloseSession` for a Steam ID that is back. Hello carries the Steam name (or "player"), not the Windows user name.
- Verified in game: guest first, host 60 s later -> joins, loads, keeps pack; host killed and restarted -> guest reconnects, reloads, keeps pack, re-announces name/look/gear. Host drops a stale connection with the same inventory key (`Kick`).
- Three players (0.3.5, one PC, ~5 GB free with two running): a third copy joined a running session as player 2 and got house/story/trials/containers/places/stations. 3 x 150 concurrent fuzz edits: houses identical (hash 91ee14d5c7c9faed on all three); an earlier run differed only by a 0.001 deg rotation rounding in one block's dump (dev hash artifact). Fuzz needs wood in each pack (`give Item_Collected_Basic_Wood 640`), else TryCommitSpec fails with refusal None.
- Third player killed and relaunched: rejoined as player 3, old player 2 timed out after and its puppet went away on both others; inventory kept (541 wood). The rejoining guest synced twice: once on `ApplyLoadedSaveGame`, again on the `FinishGenJump` that follows; harmless (same result), but wasteful.

## Transports and Steam (protocol v25)
- `Net/ITransport`: `UdpTransport` (LiteNetLib), `SteamTransport` (+ `SteamWire`, flat ISteamNetworkingMessages P/Invoke into the game's `steam_api64`), `MultiTransport` (host takes both; conn = slot * 2^20 + conn).
- SteamTransport adds what Messages lacks: Connect/Accept handshake, `Bye` carrying the farewell, 20 s silence timeout, `Part` splitting (Steam caps a message at 512 KB; world/place snapshots run to ~400 KB+). Host accepts any session request (lobby is friends-only, Hello checks version) and also sends a `Probe` to each lobby member (sender-first opens the session even if the callback never fires).
- `[UnmanagedCallersOnly]` clashes with a same-named type in UnityEngine.CoreModule's interop; the session-request callback is a kept-alive delegate via `Marshal.GetFunctionPointerForDelegate`.
- `Steam/SteamLobby`: Facepunch events (`SteamMatchmaking.add_OnLobbyCreated` etc. via `DelegateSupport.ConvertDelegate`, handlers kept alive, work queued to the main thread). Host: `CreateLobbyAsync(4)` -> friends-only, joinable, data `reka_coop=<protocol>`, rich presence `connect=+connect_lobby <id>`; "Invite friends" IMGUI button -> `OpenGameInviteOverlay`. Guest: `+connect_lobby <id>` on the command line (Steam adds it when starting from an invite / Join Game) or `-coopguest` + in-game accept -> `JoinLobbyAsync` -> `OnLobbyEntered` -> version check -> connect to `lobby.Owner`.
- Save safety: the guest role (and so the save sandbox) is fixed at launch. A join request in a game not started as a guest only shows a message.
- Verified on one PC: lobby created, data read back, button shows, UDP guest unaffected. Not verified (needs a second account): a guest over Steam, overlay invite, session callback, relay ordering.
- Offline tests (`mod/test.ps1`): every session test over UDP loopback and an in-memory fake Steam, plus reject, silent host loss and a mixed host.

## Tutorial steps, story pickups, candle rings (protocol v50, 0.3.15)
- TutorialSync: tutorial progress is (block, solved tasks). The host sends its own on change and every 3 s; a guest that got ahead sends its own. A copy behind steps forward one step a second (SolveCurrentTask, or StartNextTutBlock once a block is done, then RefreshTutDisplay), so each copy's step callbacks (doors, next talk) run there. If SolveCurrentTask throws (seen on a guest whose task never started), it does StartTutTask + TutTask.Solve. TutorialManager_v2 left SaveableSync: restoring it set the numbers without running steps.
- StoryInteract: a guest's PlayerDetection.TryToInteract/SimulateInteraction on a story object (OnTryToInteract calls CollectibleJournal, Poi_Tutorial_TeleportTalisman or GivePlayerItemsHelper) goes to the host as DialogueSync Act.Interact, which runs SimulateInteraction there. Allowed mid-conversation.
- GenericPOIFunctions.TakeItemFromPlayer: a guest without enough of the item runs it locally (the game's "you don't have" line) instead of asking the host.
- Talk leader: the host's own detector entries note the host (a guest's Lutki detector entries were leading the host's talks). Gather leaves anyone over 40 m from the leader where they are.
- PoiSync: GeneralAIController element ints are left out of the signature (AI churn re-sent whole places); an arriving State keeps the max of each UseItemInteractArea int (candle rings only fill).
- DetectorSync: a guest's blocked detector enter is also sent to the host (DialogueSync Act.Enter). The host ignores it if its copy of the detector is on (it polls the guest itself); if off, it switches the path on and runs OnEntered led by that guest. Needed where only the guest's copy switched a detector on (the ritual egg's pickup switches on the oven's).
- Dev: `listeners <method part>` lists scene UnityEvents whose saved calls name that method (found the egg's OnPickUp -> RitualEggSpot.SetActive).
- Tool: the scratchpad finder prints a type's methods with signatures via `finder <dll> T:<Type>` (nested: `Outer+Inner`).

## Log reporter (MsgType 46 Report, server not deployed)
- `Diagnostics/`: runs whenever the mod is on. `SessionLog` is a BepInEx log listener writing this run's raw lines to `BepInEx/RekaCoop-reports/current/<run>/log.txt` (the mod's lines plus warnings and errors), scrubbed before upload (`Scrubber`: Steam ids hashed, Windows user and PC names, IPs, player names -> "player N").
- Bundled (`Bundle`: gzip JSON, schema 1, under 900 KB, oldest lines dropped first) on quit, on an unhandled managed exception, on F8, and for a crashed run on the next launch.
- `Uploader`: outbox/ -> `POST <Endpoint>/v1/report` on a background thread; 2xx -> sent/ (last 20 kept), 408/429/5xx/offline -> stays for the next launch, other 4xx (426 = too old) -> rejected/. Nothing is sent before the first-launch notice was shown, nor while `Endpoint` is empty (the default until the server is deployed).
- F8: one-line IMGUI box; Enter writes a `===== REPORT <id>` marker and sends `Report` (Problem: id, playerId, note); the host relays a guest's (forcing its real id) and every copy uploads under that id.
- Server: `server/log-reporter/` (Worker + KV + rate limit, `pull.py`); see its README. Deployed 2026-11-12. Was: tested under Node with mock bindings; not deployed, not tried in a running game.

## Install-and-play hosting (protocol v53, 0.3.18)
- Decided by the user 2026-10-30: no launch options for players; install and uninstall only. No menu stage: a host is joinable only while in a world, and going back to the main menu disconnects everyone (they rejoin from Steam).
- `Instance.Auto` (no co-op options on the command line): Steam-only session (no UDP, so no firewall prompt), `Session.Host(0)` at start with `Open = false`. `CoopDriver.WatchHosting` opens it when `WorldTransfer.HostInWorld` and closes it when the main menu is up (not while travelling or loading: no menu then), with `Session.Close(reason)`: every guest gets a Reject carrying the reason.
- `SteamLobby.KeepLobby`: a friends-only lobby (and rich presence "Join Game") only while the session is open; left when it closes.
- Joining: Steam's join request at the main menu (or `+connect_lobby` at launch, handled once Steam is up) -> `CoopDriver.BecomeGuest` (Role Client, `SaveIsolation.UseGuestRoot`: `FileIO.SaveFilesRootPath` and `SaveRootOverride` -> CoopSaves/guest, rescan). In a world of one's own: told to go to the main menu first.
- A guest whose host ends the session (Reject) shows the reason, goes back to the main menu (`PauseMenu.BackToMainMenu`) and becomes a closed host again on its own saves (`UseOwnRoot`).
- A closed host drops a Hello without a Reject, so a guest started early keeps retrying (dev UDP guests, a lobby that just closed).
- Dev flags (`-coophost`, `-coopconnect`, `-coopinstance`, `-coopguest`) still work as before, with the same open-in-world gate. Two copies on one Steam account can't test the Steam invite path; that needs two accounts.
- Player package: `Enable Co-op.bat` / `Disable Co-op.bat` (rename the plugin dll to `.off`, other BepInEx mods unaffected), `Uninstall Co-op.bat` (removes BepInEx, dotnet, doorstop files, CoopSaves; saves untouched).
- The main menu dims anything drawn with IMGUI, so the mod's notices are faint there; a uGUI canvas would fix it.

## Popups, choices, stats, hut, gear effects, map (protocol v51-52, 0.3.16-0.3.17)
- Popups (PopupSync): the host's chapter (`PopUp_Chapter.QueuePopUp`), unlock (`PopUp_UseItemUnlock.QueuePopUp`) and message popups (`PopUp_Generic` / `PopUp_GenericWithSprite.QueuePopUp`) go to guests; a guest's own copy of the same popup within 15 s is skipped. Messages show with a single Continue.
- Choices (`PopUp_Decision`): guests never raise their own; the host's goes to the leader, the others watch, and every box closes when the choice is made (`ButtonClicked` postfix).
- Trial stats: the host sends its counts every 1 s (and on Ready); guests take the higher of theirs and the host's, so task counters match.
- Hut sample carries the sit clearing (`ChickenLegsBehaviour.SetSitDownFlattenPos`) and each animator layer's normalized time; the guest crossfades into the host's state at the host's time.
- Gear effects: up to 8 `VisualEffect`s per gear object, a bit each when playing (`HasAnySystemAwake`); puppets Play/Stop their copies. The candle flame works. The raised lantern's glow (`vfx_LanternBright`) won't wake on a puppet's copy (Play, Reinit, re-assigning the asset, toggling: `aliveParticleCount` stays -2) though the guest's own glows. Not solved.
- Map highlights (MapSync, v52): `CompassLogic` is static; `allElements` (IDs like "Village (0)", the same on both copies) and `SetHighlight(id, on)`. Each copy polls the highlighted set every 0.5 s and sends changes; the host relays and answers a guest's Ready with its full set. The game allows one highlight at a time, so highlighting one clears the other everywhere. Verified both ways locally.
- Travel guards: a guest that hasn't sent its body for 1.5 s (`PlayerSync.InWorldNow`) neither leads talks nor is put in the host's detectors; animal observation detectors don't pick talk leaders.
- Dev: `vfxstate <name> [reinit|play|stop|reasset|toggle|event e]` (actions on puppets' copies), `map [id 0|1]`, `fields T invoke M args...` (int/float/bool args), `popuptest`, `listeners`, `tutorial`.
- InventoryManager's slot delete only throws items away (confirm popup); there's no drop-to-world from the pack, so issue 17 needs a repro.

## Decisions (user, 2026-10-26)
1. Inventory: per-player, stored in the host's save keyed by Steam ID. Quest rewards granted to every player (own copies). World pickups first-come.
2. Dialogue: host-only; guests are pulled into the conversation and see lines and the host's choices live. Changed 2026-10-27: whoever starts a conversation leads it (picks the options); it still runs once, on the host.
3. Design for 4 players, test with 2.
4. Guests never write their own saves.
5. (2026-10-29) The building tutorial is always skipped in co-op. To do: when connected, answer the game's skip prompt (TutorialManager_v2.ShowBuildingTutorialSkipPrompt) with SkipBuildingTutorial on the host without showing it. Taking the tutorial takes away and gives back building blocks on each copy separately, and its build-count tasks are per copy.

## Hook notes
- `PlayerMovement_2021`: root-motion driven (OnAnimatorMove), `playerAnimator`, `isGrounded`, anim hashes as statics (ANIM_*), and `static Nullable<Vector2> DebugMoveInputOverride` — an input injection point for bots.
- `CharacterAssembler` has `animator`; appearance from `SaveGame.saveableCharacter`.
- `MainMenu.LoadSaveGame(folderIndex, slotIndex)` loads a save; `FileIO.TryGetMostRecentSave`.

## Unknowns
- Facepunch lobby methods: callable (CreateLobbyAsync, SetData/GetData, rich presence verified).
- World gen deterministic from seed?
- Save parts compressed?

## Guest-started NPC talks keep the game's callbacks (2026-11-03, Reka 1.0.32)
- Baba Jaga's door talk is started by her AI (`GeneralAIController`, set by `Baba Jaga_AtHome/PlayerDetector` → `POI_AIFunctions.SetAIBehaviour`) through `DialogueFunctions.StartDefaultDialogue(key, onFinished, ...)`. The AI's `onFinished` is what opens the yard gate (`AnimatedHingeJoint.Unlock/Open` on `FenceDoor`, `Tutorial_FenceGateLock` completed).
- A guest's copy blocked that call and the host replayed it without callbacks (they belong to the guest's copy), so a guest-led door talk left the gate shut on both: softlock. Host-led talks were fine.
- Now the host holds a guest's `StartDefaultDialogue` for up to 3 s and stands the host's player on the guest's puppet meanwhile; the host's AI notices its own player and starts the same talk with its callbacks, and the guest's copy is dropped ("started here by itself"). Only if nothing starts does the callback-less replay run. Verified with a guest-led door talk: gate opened on the host and, through the gate sync, on the guest.
- 1.0.32 also made the gate save its own state and added a journal-pending path (`OpenAfterJournal`, `OPEN_DELAY_AFTER_JOURNAL_SECONDS`). Dev: `gates`, `[gate]` trace lines.
