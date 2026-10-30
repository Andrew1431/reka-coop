# Getting a post-tutorial test save

Goal: a save with the hut unlocked (ritual done), on a generated map, clock running, a few use-items unlocked.
Start point: `CoopSaves/host/- 1 -/1` (tutorial map `isTutorial=true`, `currentTutBlockIndex=2`, ritual not started,
`hutParked=true`, `manualOverride=true`).

Static research only, nothing launched. Tags: **[V]** seen in a signature, enum, save data, or the IL2CPP xref scan
(call targets, string literals and class usages per method, decoded from `BepInEx/interop/MethodXrefScanCache.db` +
`MethodAddressToToken.db` + GameAssembly metadata usages). **[I]** inferred (needs a runtime check).
The xref scan shows *which* classes/methods/strings a method touches, not *which field* of a static class it touches,
so "invokes GameEvents.X.Y" is [I] whenever only the class `GameEvents/X` is visible.

## 1. What exists

### Built-in skip entry points
| Method | What the scan shows | Verdict |
|---|---|---|
| `static void SaveLoadGame.SkipTutorialAndJumpToFirstMap()` | touches only `GameEvents/Map` (x2) and `GameEvents/Tutorial` [V]; no code callers, no delegate creation (designer/UnityEvent hook) [V] | Almost certainly `GameEvents.Tutorial.OnSkippedTutorial?.Invoke()` + `GameEvents.Map.TriggerJumpToNewMap?.Invoke()` [I]. **Best single call.** |
| `Cheats.SkipTutorial()` (instance) | logs "SkipTutorial method called", notification "Skipping to Trial 1: TrialOfLeshy", writes a `Cheats` static [V] | Only sets `static int Cheats.SkipTo` (=1 [I]); does not skip by itself. Siblings `SkipTrialOfLeshy` ("Trial 2: LearningTheRopes"), `SkipEverything`, … same pattern [V]. |
| `TasksTracker.OnSkippedTutorial()` | subscribed in `TasksTracker.Awake` to a `GameEvents/Tutorial` event [V]; logs "MapFlow: Skipping tutorial trial", calls `TrySkipTutorialTrial()` → `SkipCurrentTrial()` → `SolveTrial(int,bool)`, reads `Cheats` statics + `ContentBlocker`, `SetTrial(int)`, `Trial.RestoreAsCompleted()`, `UseItemManager.UnlockItem("Gardening"/"WateringCan"/"BrokenDziadyMask")`, `StaticGameCondition.SetCondition("Learn_HelpGhosts"/"Learn_SeeGhosts", …)` [V] | The skip logic proper. The later unlocks are presumably gated on `Cheats.SkipTo` [I]. `TrySkipTutorialTrial` only acts when `CurrentTrialIndex == 0` (string "Trying to skip tutorial trial but current trial index is ") [V string, I logic]. |
| `GiveStartingItemsOnSkippedTutorial` (scene component) | `Start` subscribes `GiveItems` to a `GameEvents/Tutorial` event [V]; `GiveItems` adds BBs (`PlayerBBCollection.AddAsInstance/AddAsPlan`) and subscribes `GiveStartingItems` to a `GameEvents/Map` event [V]; `GiveStartingItems` calls `UseItemManager.UnlockItem` for **HandLantern, PlayerBirds, Backport, PineBranch, Fire, BuildMode, HouseBuilding, LarchCones** [V] | This is the "few tools unlocked" we want, delivered on the next map load [I]. Present in GameScene? [I] |
| `DoSthOnSkippedTutorial`, `TutorialSkipper_DoSthOnSkip`, `DemoStartScreen.OnSkippedTutorial` | subscribe to `GameEvents/Tutorial` events, fire UnityEvents [V] | Scene-side reactions (unknown content) [I]. |
| `DemoSkipper.SkipTutorial()` / `SkipToEmbers()` | touch many GameEvents classes + `Wait.AndRun`; `Update` reads a `Cheats` static and a Rewired button [V] | Demo-build path; not needed. |
| `MapChanger.JumpToNewMap()` | `MapChanger.Awake` subscribes `JumpToTutorial`, `JumpToCharacterCreation`, `JumpToNewMap`, `JumpToSameMap` to `GameEvents/Map` events [V]; body: `LeaveCurrentMap()`, "MapFlow: Jump", `SetTerrainData`, `Wait.AndRun`, subscribes `FinishGenJump` to a `GameEvents/Environment` event [V] | Direct new-map jump. `Cheats.GenerateNewMap()` only touches `GameEvents/Map` [V] (= invoke `TriggerJumpToNewMap` [I]). |
| `GameEvents.Cheats.SkipCurrentChickenRitualStep`, `DebugTutorialRitualSkipper.SkipNext()`, `RitualProgressionAdvancer.Advance()` | `SkipNext` steps through `GameEvents.Tutorial.Ritual_On*` events by name (PlacedChicken … RitualFinished) [V strings] | Authentic ritual completion one step at a time; many steps, needs the ritual to be running (ours is pre-ritual). Fallback only. |
| `TutorialManager_v2.SkipBuildingTutorial()`, `SkipTo_GetEmbers()`; `SaveLoadGame.SkipToBuildingTutorial()` | [V names] | Post-ritual building-tutorial skip; only relevant for the authentic path. |

Cheat taint: `CheatTracker.MarkCheatUsed()` is called only from `CheatSetUp.Run()` and a `Cheats.Awake` lambda [V].
Calling the methods above directly does **not** set `cheatsUsedOnThisSave` [I from that]. Avoid `CheatSetUp.Run()` /
`MarketingCheatsTab` buttons (they mark the save and pop a dialog).

Debug panels (`DebugPanel`, IMGUI, `static KeyCode TOGGLE_KEY`, sections registered via `Debug*Section.Register()`;
`MarketingMenu` lists `Cheats.allCheats` as `CheatSetUp`) exist [V], but only wrap the same calls; reaching them needs
the toggle key and `Cheats.allowCheats`. Not needed: call the targets directly. `BenchmarkDriver` travels to seeded maps
from a JSON config (`BenchmarkScenario.savePath/seed/chapter`) — heavier than needed.

### How the ritual flag is stored [V names, I flow]
- Runtime: `static bool RitualIdentifier.ritualFinished`, `static bool RitualIdentifier.isRitual`; `RitualIdentifier.OnRitualFinished()`
  writes them, subscribed to a `GameEvents/Tutorial` event (= `Ritual_OnRitualFinished` [I]).
- Save: `SaveLoadGame.FinishSaveGame(SaveGame)` reads `TutorialIdentifier` + `RitualIdentifier` statics [V] → writes
  `SaveableProgress.hasCompletedChickenLegsRitual` / `chickenLegsRitualInProgress` [I]. `TasksTracker.ISaveable_RestoreSavedState`
  reads `RitualIdentifier` + `TutorialSaveState` getters [V] (restores it [I]).
- `DeactivateHomeForTutorial` (the home/hut GameObject): `Awake` subscribes `DisableThisGameObject` and `ReactivateThisGameObject`
  to two `GameEvents/Tutorial` events [V]; `ISaveable_RestoreSavedState` calls `ReactivateThisGameObject()` [V] (conditionally [I]).
- `HouseAndPlayerOntoTerrainMover.Awake` subscribes `MoveHouseOntoTerrain()` to a `GameEvents/Map` and a `GameEvents/Home` event [V]
  → hut is put onto the new terrain after a jump [I].

### How the tutorial ends in normal play [I, from names/strings]
Ritual finale (`Ritual_OnRitualFinished`, `TriggerSetActiveChickenHouse`, "Ritual.GetChickenLegsAndStandUp") → building tutorial
(`OnFinished_RebuildShack/BasicBuilding/BuildingAndDecorating`, `OnFinishedTutorial`) → task texts
`C0T1_Text_TalkedToJagaToMoveOn` / `C0T1_Text_ControlChickenToMoveOn` → the player steers the hut past
`BeyondEdgeOfMapHouseTravelTrigger.mapEdgeStartsAtRadius` → `Update` → `Trigger()` popup "Do you want to leave this map and travel
onwards?" [V string] → `Confirm()` → `GameEvents/Home` event after `Wait.AndRun` [V] → travel → `MapChanger.JumpToNewMap` [I].
`BB_Logic_TravelStation.SetFirstDestinationIfNoTutorialMap` ("MapFlow: SetUpDestinations (No Tutorial Map)") prepares the first destination [V].

### Load-time behaviour relevant to save editing [V strings in `SaveLoadGame.ApplyLoadedSaveGame`]
"MAP DATA IS OK" / **"MAP DATA WAS NULL: Will jump to first map."** (+ `DiscardedMapPopUpText` popup) / "MAP DATA WAS OLD: Could not load
map. Generating new one instead." / "TUTORIAL MAP DATA IS OK" / "TUTORIAL MAP DATA WAS OLD: Resetting tutorial but skipping to building
tutorial since ritual was complete." The tutorial branch is chosen from the map (`SaveableMap.isTutorial`) [I]. Whether a *missing*
`CurrentMap_c.xml` reaches the "null" branch or the `saveFilesMissingText` error is unknown [I].

## 2. Options compared
| | Cost | Reliability | Notes |
|---|---|---|---|
| (a) debug-panel / `Cheats` methods | low | medium | `Cheats.SkipTutorial` only sets `SkipTo`; needs the `Cheats` instance (may be destroyed in release, `Cheats.Awake` calls `Object.Destroy` conditionally [V]). Buttons mark the save cheated. |
| (b) edit Progress.xml + drop map | low | medium-low | Bypasses OnSkippedTutorial (no trial skip bookkeeping, no starting items), relies on unverified null-map path and on `DeactivateHomeForTutorial` reactivating from the flag. Good fallback. |
| **(c) game methods via cmd hook** | low | **high** | Runs the game's own skip handlers (`TasksTracker.OnSkippedTutorial`, `GiveStartingItemsOnSkippedTutorial`, `MapChanger.JumpToNewMap`), then the game writes a consistent save. |

## 3. Recommended steps (c) — host copy only, one instance is enough
Back up `game/host/CoopSaves/host/- 1 -/` first (the save in step 7 overwrites slot 1). Add these as commands in
`Dev/Commands.cs` (all calls are on the main thread from `Commands.Tick`):

1. Load the tutorial save (`-coopautoload`), wait for `GameEvents.SaveSystem.OnLoadFullyComplete` / player present.
2. `skipprep`: log then set
   - `Cheats.SkipTo = 1;` (static int [V]; target trial = Leshy [I from "Skipping to Trial 1: TrialOfLeshy"]). Log the old value.
   - `RitualIdentifier.ritualFinished = true; RitualIdentifier.isRitual = false;` (statics [V]) so FinishSaveGame records the ritual as done [I].
3. `skiptut`: `SaveLoadGame.SkipTutorialAndJumpToFirstMap();` (static, no args [V]).
   - Equivalent manual form if it does nothing: `GameEvents.Tutorial.OnSkippedTutorial?.Invoke();` then
     `GameEvents.Map.TriggerJumpToNewMap?.Invoke();` (static `Il2CppSystem.Action` props [V]) or
     `Object.FindFirstObjectByType<MapChanger>().JumpToNewMap();` [V].
   - Expect log lines: "MapFlow: Skipping tutorial trial", "<b>MapFlow: Set Trial: </b>…", " MapFlow: Jump" [V strings].
4. Wait for `GameEvents.Map.OnMapFullyLoaded` (or poll `!SaveLoadGame.worldIsLoading` + `MapChanger.isJumping == false`).
5. `skipcheck`: log `TutorialIdentifier.isTutorial` (want false), `FindFirstObjectByType<MapLoader>().lastGeneratedWasTutorial` (false),
   `TasksTracker.CurrentChapterTag` (want `Leshy`=20) and instance `CurrentTrialIndex` (1), `RitualIdentifier.ritualFinished`,
   `FindFirstObjectByType<ChickenLegsBehaviour>()` non-null + `gameObject.activeInHierarchy` + position, `WeatherManager.manualOverride`
   and `dayTime` twice 10 s apart, and `UseItemManager.Instance.GetItemByID("Backport")` unlocked state.
6. Repair anything missing:
   - Hut inactive: `foreach (var d in Resources.FindObjectsOfTypeAll<DeactivateHomeForTutorial>()) d.ReactivateThisGameObject();`
     and/or `GameEvents.Tutorial.TriggerSetActiveChickenHouse?.Invoke();` then `GameEvents.Home.TeleportToPosition?.Invoke(playerPos + fwd*15)`
     (or `MarketingHutTab.TeleportToPlayer()` [V static]).
   - Items: `foreach (var id in new[]{"HandLantern","PlayerBirds","Backport","PineBranch","Fire","BuildMode","HouseBuilding","LarchCones"}) UseItemManager.Instance.UnlockItem(id);`
     (also `SaveLoadGame.RITUAL_GRANTED_USE_ITEMS`, static string[] [V]).
   - Clock frozen: `w.manualOverride = false; w.keepManualOverride = false;` (fields [V]).
   - Trial still 0: `FindFirstObjectByType<TasksTracker>().SkipTutorialTrial();` [V] (or `SetTrial(1)`).
7. `save`: `var s = Object.FindFirstObjectByType<SaveLoadGame>(); s.SaveCurrentSaveGame(s.currentSaveGameSlot, SaveGameType.ManualSave);`
   (`SaveGameType` ManualSave=0/AutoSave=1/QuickSave=2 [V]); wait for `GameEvents.SaveSystem.OnGameSavedSuccessfully`. If refused
   (`SavingIsForbidden`), `GameEvents.SaveSystem.ForbidSaving?.Invoke(false)` and retry.
8. Verify on disk: Progress.xml `hasCompletedChickenLegsRitual=true`, `currentTrialIndex=1`, `cheatsUsedOnThisSave=false`;
   `CurrentMap_c.xml` (gzip) `isTutorial=false`, `hutParked=false`, a `mapGenState` block. Reload once to confirm, then copy the
   slot folder to `game/client/CoopSaves/...` for the client.

## 4. Fallback (b): save edit, then let the game generate a map
In a copy of the slot (never the original):
- Progress.xml: `hasCompletedChickenLegsRitual` true, `chickenLegsRitualInProgress` false, `currentTrialIndex` 1, `completedTrialOnCurrentMap` false,
  `buildingTutorialSkipped` true, `hasAskedBuildingTutorialSkip` true, `backport_forbidden` false, `backport_teleportToOverwrite` false,
  `<unlocked>true</unlocked>` for Backport, BuildMode, HouseBuilding, PineBranch, Fire, PlayerBirds, LarchCones.
  Leave `currentTutBlockIndex` (block count unknown); `staticGameInformation` unchanged.
- Meta.xml: `currentTrialIndex` 1.
- Remove `CurrentMap_c.xml` and `FogMap.xml` → load → expect "MAP DATA WAS NULL: Will jump to first map." and a popup; dismiss it.
- If the load errors on the missing file instead, restore it and after load run step 3 of (c) (`MapChanger.JumpToNewMap()`) — the
  edited Progress flags still apply.
- Then steps 5–8 above.

## 5. Second fallback: authentic ritual
`TutorialManager_v2.SkipTo_GetEmbers()` / `GameEvents.Tutorial.SkipTo_GetEmbers` → repeatedly `GameEvents.Cheats.SkipCurrentChickenRitualStep?.Invoke()`
(or invoke `GameEvents.Tutorial.Ritual_On*` in the `DebugTutorialRitualSkipper` order) until `Ritual_OnRitualFinished` →
`TutorialManager_v2.SkipBuildingTutorial()` → `GameEvents.Map.ForceTravelToNextMap?.Invoke()` (`ForceTravelHelper` uses it [V class]).
Most faithful (hut rises via the real cutscene), most steps, most ways to stall.

## 6. Open questions (runtime)
1. Which `GameEvents.Tutorial` field each listener above is bound to (scan sees the class only).
2. Default value of `Cheats.SkipTo`, and whether `Cheats.instance` exists in this release build.
3. Whether `SkipTutorialAndJumpToFirstMap` runs cleanly from the middle of the tutorial (block 2) or leaves tutorial UI prompts.
4. Whether `WeatherManager.manualOverride` clears on map change (`UpdateWeatherOnMapChange` [V name]).
5. Missing `CurrentMap_c.xml` → null-map branch or load failure.

## Tooling note
Scratch tool (not in repo): Cecil reads `CachedScanResultsAttribute` (XrefRangeStart/End, RefRangeStart/End) per interop method;
`MethodXrefScanCache.db` = 16-byte header + 24-byte records `{long ptr, long foundAt, long type}` (type 1 call, 0 global);
`MethodAddressToToken.db` = header `{magic,ver,?,count,dataOff}`, assembly names, `long[count]` sorted RVAs, then `(int token,int asmIndex)[count]`.
Globals: read the 8-byte slot at the RVA in GameAssembly.dll → encoded usage `(type<<29)|(index<<1)|1`; type 5 = string literal
(global-metadata header pair 0/1), type 1 = Il2CppType index into `s_Il2CppMetadataRegistration.types` (found at file 0x59f7f40,
typeDefinition 88 bytes, methodDefinition 36 bytes in metadata v31), type 3 = method definition index.

## 7. Follow-up after the first runtime test (skip worked; save refused, hut inactive)

### 7.1 Why `SaveCurrentSaveGame` does nothing [V call scan]
- `SaveLoadGame.SaveCurrentSaveGame(int, SaveGameType)` calls `RefuseWhileCollecting(type)` then `get_SavingIsRefusedNow()` and returns
  early when it is true (other early outs: autosave/quicksave "in menu" via `RekaSingleton<CurrentGameMode>`).
- `SaveLoadGame.get_SavingIsRefusedNow()` reads exactly three things: **`SaveBlockers.IsBlocked`** (call), a static of
  **`DialogueUILogic`** and a static of **`PopUpManager`** [V]. The statics are almost certainly `DialogueUILogic.isTalkingOrInQueue`
  and `PopUpManager.isShowingPopupOrInQueue` (the only fitting static bools) [I]. `CanKeepCollecting` uses the same getter
  ("saving became forbidden (dialogue, popup or a save blocker) while the save was collected" [V]).
- **`SaveLoadGame.LoadedToTutorial` is not read by the refusal path** [V]. It is a load-time static; it will be right after reloading the
  new save. Clearing it by hand (`SaveLoadGame.LoadedToTutorial = false`, static setter [V]) is harmless but not the fix.
- `SaveBlockers` (static class) [V]: `List<MonoBehaviour> _requests`, `List<string> _reasons`, `IsBlocked`, `Request(owner, reason)`,
  `DeRequest(owner)`, `GetDebugString()` ("Saving is blocked by N holder(s): - …"), `RemoveDeadRequests()` (drops destroyed/inactive owners).
  Requesters [V refs]: `PlayerMovement_2021.BlockMovement(bool)` and `PlayerMovement_2021.OnEnable()` (reason "player movement blocked"),
  `POI_PlayerFunctions.PlayerBlockMovementAndSaving(float)`, `SaveBlockWhileTimelinePlays.OnEnable/OnPlayed`.
  Releasers: `PlayerMovement_2021.UnblockMovement()`, `POI_PlayerFunctions.PlayerUnblockMovementAndSaving/ReleaseSaveBlock/…`,
  `SaveBlockWhileTimelinePlays.OnStoppedOrPaused/Release/OnDisable`.

Normal flow after a trial starts [V calls, I order]: chapter title card `PopUp_Chapter` ("Trial of the Forest Spirit") is a queued popup
(`PopUpManager.isShowingPopupOrInQueue` true) → player presses confirm → `PopUp_Chapter.OnConfirmPressed()` → `FinishClose()` →
`CurrentGameMode.DeRequestInGameUI`, `PopUpManager.OnFinishedShowingPopUp()`, onConfirm → `TasksTracker.OnTrialTitleCartDismissed()` →
`StartChapterIntroDialogue()` ("Starting intro dialogue for chapter: …", `StandaloneDialogue.StartDialogue()`, listener
`OnTrialIntroDialogueFinished`) → dialogue (`DialogueUILogic.isTalkingOrInQueue` true, player movement likely blocked) → dialogue ends →
`TasksTracker.OnTrialIntroDialogueFinished()` (GameEvents/Progression event, `StaticGameCondition.SetCondition`). So the refusal is most
likely the **unconfirmed title card**, then the intro dialogue. In an unfocused window nobody presses confirm.

Calls to try, in order (re-check `SavingIsRefusedNow` after each):
1. Diagnose: log `SaveBlockers.GetDebugString()`, `DialogueUILogic.isTalkingOrInQueue`, `DialogueUILogic.isPassiveDialogueOnScreen`,
   `PopUpManager.isShowingPopupOrInQueue`, `PopUpManager.IsInLoadingScreen`.
2. Title card (legit path): `Object.FindFirstObjectByType<PopUp_Chapter>()?.OnConfirmPressed();` (call twice if the first only finishes the
   open animation — it calls `FinishOpenAnimation()` first [V]). Fallback: `PopUp_Chapter.ForceClosePopUp()`, last resort
   `FindFirstObjectByType<PopUpManager>().ForceCloseAll()` (skips onConfirm, so the trial intro would not run).
3. Intro dialogue: simplest is to focus the host window and click through. Scripted: `DialogueUILogic.Instance.SkipToLastLine()` (what
   `Cheats.SkipDialogueToLastLine` does [V]), then `DialogueUILogic.Instance.StopTalkAndCallOnFinished()` (or `CallOnFinished()`) so
   `OnTrialIntroDialogueFinished` runs; `EndCurrentDialogue()` / `ForceCloseDialogue()` are fallbacks [V names, I semantics]. If the
   dialogue offers choices (`NPC_DialogueManager.PresentsDialogueOptions` is the chain target [V]) a manual click is needed.
4. If `GetDebugString()` still lists "player movement blocked": `FindFirstObjectByType<PlayerMovement_2021>().UnblockMovement();` [V].
   Other holders: `SaveBlockers.DeRequest(owner)` with the owner from `SaveBlockers._requests`.
5. Retry `SaveCurrentSaveGame(currentSaveGameSlot, SaveGameType.ManualSave)`; wait for `GameEvents.SaveSystem.OnGameSavedSuccessfully`.

### 7.2 Hut inactive after the jump
What activates it in normal play [V calls, I wiring]:
- `TutorialChickenLegsManager.GetChickenLegsAndStandUp()`: moves the legs to the manager's transform, `chickenLegs.gameObject.SetActive(true)`,
  `ChickenLegsBehaviour.StandUp()`, then `Wait.AndRun(EnableChickenLegs)` (`EnableChickenLegs` = `chickenLegs.enabled = true`). Subscribed in
  `TutorialChickenLegsManager.Awake` to a `GameEvents/Tutorial` event (the ritual finale, strings "Ritual.GetChickenLegsAndStandUp",
  "Ritual.TriggerSetActiveChickenHouse"). `CallEvent()` is the counterpart (`enabled=false`, `StartOverride()`, `SitDownImmediately()`).
  The manager lives on the tutorial map [I], so it is gone after the jump — that is why the skip leaves the hut inactive.
- `DeactivateHomeForTutorial`: `Awake` subscribes `DisableThisGameObject` and `ReactivateThisGameObject` to two `GameEvents/Tutorial` events [V];
  the only direct caller of `ReactivateThisGameObject()` is `ISaveable_RestoreSavedState(SaveGame)` [V] (on load, conditional [I]).
  It logs "RitualSave: DeactivateHomeForTutorial.ActivateThisGameObject @<path>".
- `GameEvents.Tutorial.TriggerSetActiveChickenHouse` listeners [I]: `DeactivateHomeForTutorial.ReactivateThisGameObject`,
  `RitualEndSaveState.OnChickenHouseActivated`, `Tutorial_DoSthOnChickenHutEnabled` lambda.
- `GameEvents.Tutorial.Ritual_OnRitualFinished` listeners [V subscriptions, I field]: `RitualIdentifier.OnRitualFinished` (sets statics),
  `Hud_BackportAndBuilding.OnRitualFinishedMethod` (shows backport/home HUD: `CanvasGroup.alpha`, subscribes `ActivateHomePortHud`),
  `TasksTracker.OnRitualFinished` (`WaitAndEvaluate()`), `TutorialNPCLogic.OnRitualFinished`. It does not itself activate the hut [I].
- After the hut is active, `HouseAndPlayerOntoTerrainMover.MoveHouseOntoTerrain()` (bound to a Map and a Home event [V]) places it on terrain.

Calls to try, in order:
1. `foreach (var d in Resources.FindObjectsOfTypeAll<DeactivateHomeForTutorial>()) if (d.gameObject.scene.IsValid()) d.ReactivateThisGameObject();`
   — expect the "ActivateThisGameObject" log.
2. `var legs = Resources.FindObjectsOfTypeAll<ChickenLegsBehaviour>()` (scene object): if `!legs.gameObject.activeInHierarchy`, activate the
   inactive ancestors/self with `SetActive(true)`; then `legs.enabled = true;` (= `GetChickenLegsAndStandUp` + `EnableChickenLegs`).
3. Place it: `legs.TeleportToPositionAndSnapOntoTerrain(playerPos + playerForward * 15f)` or `FindFirstObjectByType<HouseAndPlayerOntoTerrainMover>().MoveHouseOntoTerrain()`
   (or `MarketingHutTab.TeleportToPlayer()` [V static]); then `legs.SitDownImmediately()` or `legs.StandUp()`. If it stays overridden:
   `legs.StopOverride()`, `legs.SetControlMode(ControlMode.None)`.
4. Optional HUD: `FindFirstObjectByType<Hud_BackportAndBuilding>()?.OnRitualFinishedMethod();` if the backport/home HUD is missing.
   Avoid invoking `Ritual_OnRitualFinished` / `TriggerSetActiveChickenHouse` globally off the tutorial map (tutorial listeners may be stale/destroyed).
5. Save (7.1), reload once, check `DeactivateHomeForTutorial.ISaveable_RestoreSavedState` reactivates it from the save (log line above),
   map `hutParked=false`, Progress `hasCompletedChickenLegsRitual=true`.
