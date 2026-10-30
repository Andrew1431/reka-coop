> Superseded by user decisions (NOTES.md): dialogue is host-only with guests pulled in to watch; guests keep no progress. The ownership/guest-progress recommendations below are not the plan.

# Slice: Villagers, dialogue and quests

Static research only (interop signatures from game/client/BepInEx/interop/Assembly-CSharp.dll via dumper, save XML from game/host/CoopSaves/host/- 1 -/1). Tags: [V] = verified (seen in a signature, enum or save data). [I] = inferred (bodies are IL2CPP stubs, so behaviour is a guess).

## 1. Classes that own this state

### NPCs and movement
- `GeneralAIController : MonoBehaviour` [V]: the one AI class for villagers, animals and birds. `ChickenLegsAIController` (the hut) derives from it. Fields: `UnityEngine.AI.NavMeshAgent agent`, `bool dontUseNavMesh`, `AIType aiType` (Villager=0, Lumberjack=1), `VillagerBehaviour villagerBehaviour`, `AIBehaviour currentBehaviour`, `List<AIBehaviour> aiBehaviours`, `Transform defaultTarget/currentTarget`, `Vector3 targetPos`, `float behaviourTimer/behaviourTime`, `AILODLevel _lodLevel` (Full/Reduced/Minimal), `bool isTalking`, `TalkRequest _talkReq`.
  - Statics `Transform PlayerTransform`, `PlayerHeadTransform`, `TransformReference s_playerRef` [V]. The AI only knows about one player for look-at, flee, "blocked by player" and LOD [I].
  - Methods [V]: `SwitchToBehaviour(AIBehaviour, bool usePriority)`, `SwitchToBehaviourIndex(int)`, `ReturnToDefaultBehaviour(bool,bool)`, `SetTarget(Transform)`, `SetMoveGoal(Vector3 desiredWorldPos, bool forceRepath)`, `TeleportTo(Vector3,bool snap)`, `SetAIPos(Vector3,bool,bool)`, `FreezeAI()/UnfreezeAI()`, `PauseBehaviours(bool dropActivity)/UnPauseBehaviours()`, `SuspendActivity(ActivitySuspensionReason, bool keepPose)/ResumeActivity(...)`. Reasons: Dialogue, Carried, HomeRebakeHold, LODMinimal, Petted, Capture.
  - Talk methods [V]: `BeginTalk(ref TalkRequest)`, `EndTalk()`, `ReleaseDialogueHold()`, `BeginPetting(Transform petter, bool)/EndPetting()`, `BeginCarry()/EndCarry(Vector3)`.
  - Save methods [V]: `ISaveablePOIElement_GetStateForSaving(SaveableMap, poiID, elementID)`, `ISaveablePOIElement_RestoreSavedState(...)`, `ApplyRestoredActivity(SerializableAIController)`.
- `SerializableAIController` [V], stored in the map save: `uniquePoiID, uniquePoiElementID, targetIsDefault, targetPos, targetRot, behaviourIndex, behaviourName, currentPos, currentRot, waypoints, activityPhase, behaviourTime, behaviourTimer, goalOffset, activityAnimation, assignerPoiID, assignerKey`. This is already a complete per-AI net snapshot with a stable id pair.
- `AIBehaviour : ScriptableObject` [V]: data only (`behaviour`, `moveSpeedIndex`, `animations`, `behaviourTime` range, `talkResponse` Default/WakeUp/Unresponsive, `movementMode`). Resolved by name: `static ResolveSavedBehaviour(string savedName, int savedIndex, ...)`, `FindBehaviourByName(list, name)` [V].
- `VillagerBehaviour` [V]: villager schedule. `GenerateNextActivity(bool teleport, VillageActivityplace force, bool forceFar)`, `TryGenerateNextActivity(...)`, `currentActivityplace`, `MakePartOfPOI(...)`, tools `GetTool(int)/RemoveTool()`. There is no time-of-day schedule: the villager picks a random next `VillageActivityplace` (stand, farm, hammer, sit, chop wood...) [I from names; saved behaviourNames confirm the activity names V].
- `POI_VillagerAssigner` [V]: binds a generic villager to a POI or quest role (`villagerAI`, `dialogues`, `dialogueOptions`, `disableDialogueManager`, `characterGenerationDefinition`).
- `WildAnimalGroup` [V]: `SpawnAnimals()`, `static Random HerdRandom(Vector3 position)`, `spawnedAnimals`, `randomWanderInterval`, `dayTimeBiomes`. Spawns are seeded by position, so the same on both peers [I]. Wandering after spawn is not seeded [I].
- `HomeAIManager` (singleton) [V]: AIs at the home/hut. `Register/Unregister(GeneralAIController)`, `SuspendAll/ResumeAll`, `OnPlayerEnteredHomeBase/ExitedHomeBase`, `PlayerAtHomeBase`.
- Navmesh is baked at runtime [V]: `RuntimeNavMeshBaker`, `HomeNavMeshBaker`, `NavMeshRebakeTrigger`, `GeneralAIController.OnWorldNavMeshRebuiltLocally(Bounds)`.
- Other NPC behaviours [V]: `CandleCreatureBehaviour`, `SickAnimal`, `SickNPC`, `FoodVillagerLogic`, `AnimalAtHome`, `TutorialNPCLogic` (Baba Yaga; `MoveTo/MoveToUnseen(BabaLocation)`, teleports when the camera cannot see it via `IsUnseenByCamera(Vector3)`).
- Saved map data [V]: 56 `SerializableAIController` entries in a fresh tutorial map (crows, goats, Lutki, villagers at activity places).

### Dialogue (custom tech: no Yarn, Ink or Fungus)
- No Yarn, Ink, Fungus or NodeCanvas assembly or metadata string [V]. Text is I2 Localization terms (`I2.Loc.LocalizedString`). Reveal uses Febucci TextAnimator. Branching uses UnityEvents in POI prefabs [V types; graph by UnityEvent is I].
- `DialogueFunctions : MonoBehaviour` [V] is the base class of `NPC_DialogueManager`, `StandaloneDialogue`, `POI_Dialogue`, `TutorialNPCLogic`, `AdvancedNPC_DialoguesAndActions`, `FoodVillagerSequence`. Methods: `StartDefaultDialogue(LocalizedString text, Action action, bool isPassiveDialogue, Nullable<DialogueBranch> afterDialogue, bool dontAutoStopTalkingVisuals, bool isSecondaryCharacter, Action onStarted)` (also a string overload with `lineEmotions`), `EndDialogue()`, `NPCAITalk()`, `NPCAIStopTalk(bool)`, `DisableDialogueCamera()`. Flags: `useDialogueCam`, `rotatePlayerToAI`, `rotateTowardsPlayer`, `lookAtCharacter`.
- `NPC_DialogueManager` [V] runs rumour/option conversations. `myRumors`, `priorityRumor`, `lineIndex`, `cantTalk`, `notTalkingToPlayerAnymore`, `IsPersistentNPC`. Methods: `PresentsDialogueOptions(List<Rumor>, bool customRumors)`, `StartRumor(int index)`, `StartRumor(Rumor)`, `StartPriorityRumor()`, `ContinueDialogue(int, List<LocalizedString>, Action)`, `SayGoodbye()`, `ImmediateGoodbye()`, `SayLineActively(LocalizedString, Action, bool)`, `SayLinePassively(...)`, `SetNPCCanNotTalk/SetNPCCanTalkAgain`, `AddRumor/RedactSingleRumorForThisNPCOnly`.
- `RumorCollection` (static `Instance`) [V] holds the global pool `availableRumors`: `Add(Rumor)`, `RedactRumor(Rumor)`, `GetRumors(RecipientType, NPC_DialogueManager, List)`. `RumorCollection/Rumor` has `prompt, rumor, continuationDialogue, priorityRumor, thisRumorEndsDialogue, onRumorHeard/onRumorStarted (UnityEvent), decisionToTriggerOnLastLineOfDialogue (POI_Decision), PlayerHasHeardRumor`, and `OnHeard()/OnStarted()/StartMe()/RemoveMe()`.
- Choices [V]: dialogue options are rumours shown as a list (`DialogueContainer.dialogueOptions`, `DialogueOptionSetter.AddToNPC/RemoveFromNPC`). Binary choices are `POI_Decision` (`option1Text/option2Text`, `TriggerOption1()/TriggerOption2()`, UnityEvents `OnOption1/OnOption2`).
- `StandaloneDialogue` [V]: one-shot POI lines. `StartDialogue()`, `onlyActivateOnce`, `hasBeenActivated` (saved, `STATE_ACTIVATED`), `decisionToTriggerOnLastLineOfDialogue`, `dialogueContinuation`. `POI_DialogueSequence.StartSequence()` chains them.
- `DialogueUILogic : HudElement` (singleton `Instance`) [V] is the single on-screen dialogue box. `RequestStartDialogue(lines, names, titles, onFinished, afterDialogue, ...)`, `StartDialogue(...)`, `EndCurrentDialogue()`, `ForceCloseDialogue()`, `SkipToLastLine()`, `QueueNextLine(bool isPassive)`. Static `isTalkingOrInQueue`, `isPassiveDialogueOnScreen`. Advance uses Rewired `rwPlayer` input (`WaitForPlayerInputThenDoSth`). Watchdog `WatchdogForceRecover()`. Test hook: `static bool AutoAdvanceForTesting` [V], useful for bots.
- `SaveLoad_HeardDialoguePerMap` [V]: `static List<string> heardDialogueIDs`, `static HasHeard(string term)`, `static SetHeard(string term, bool)`.
- Game event bus [V], usable as postfix-free hooks: `GameEvents/UI/PopUps.OnDialogueOpened`, `OnDialogueClosed`, `OnDialogueLineStarted(string,string,bool,int)`, `OnShowRumorPopUp`; `GameEvents/AI.OnTalkRequested(GeneralAIController)`; `GameEvents/UI/Interactables/InteractableGeneric.StartDialogueInUi(string)`; `GameEvents/SaveSystem.OnTriedToSaveWhileForbidden_Dialogue`.

### Camera takeover and pausing (co-op hazards)
- `DialogueCamera` [V]: Cinemachine `_vcam` and `_brain`, `ThirdPersonCameraController freeCamController`, `PlayerMovement_2021 _playerMovement`. `Activate(Transform head, string speaker, DialogueShotFamilyOverride, Transform listenerHead)`, `Deactivate()`, `BindReka(float)`, `SuppressRekaIdleRandoms()`. It takes over the main camera and drives Reka's eyes and idles during talk. `NPC_DialogueManager.useCustomDialogueCamera/customDialogueCam` swap in authored cameras [V].
- `CurrentGameMode` (CurrentGameMode.dll) [V]: modes Regular, Building, InGameUI, Menu, UsingRadialMenu, LoadingScreen, set by `RequestInGameUI(MonoBehaviour)` and `RequestMenu()`. Dialogue most likely requests InGameUI, which blocks player movement [I; `DialogueUILogic.TryPopQueueOnGameModeChanged(GameMode)` V].
- Pause [V]: `PauseMenu.OpenPauseMenu/ClosePauseMenu`, `GameEvents/UI/PauseMenu.OnUiOpened_Pause`. `PauseFmodEventWhileGamePaused` describes "Pauses the sound while Time.timeScale is 0" (metadata string). So the pause menu sets `Time.timeScale = 0` [I, strong]. In co-op that freezes the whole host world.
- Timelines and title cards [V]: `SaveLoadTimeline` / `SerializableTimeline{time, playing}` (POI cutscenes with saved progress), `SaveBlockWhileTimelinePlays`, `TasksTracker.ShowChapterPopUp(...)`, `RunTitleCardFollowUpOnceLoaded`, `CanvasManager/PauseForbidReason` (StartScreen, PopUp, EnvironmentGeneration, DemoEndScreen, NewGameIntro).
- `TutorialNPCLogic.IsUnseenByCamera(Vector3)` teleports Baba Yaga when the local camera cannot see her [V]. The remote player might see her pop [I].

### Quests, tasks, chapters, flags
- `TasksTracker` [V] runs the main story. Static `ChapterState currentChapterState` (None, InProgress_withTrialLocation, InProgress_withoutTrialLocation, Completed, CompletedEverything) and static `ChapterTag CurrentChapterTag` (TrialOfTheChicken=10 ... TheWait=120, Sandbox=9999). Instance: `List<Trial> trials`, `_currentTrialIndex`, `claimedRewards bool[]`, the tutorial bools/ints (`tutorial_gotChicken`, `tutorial_offeringsPlaced`, ...), and `static List<QuestStepID> completedSteps`.
  - Methods: `Evaluate()`, `EvaluateInFiveFrames()`, `OnFinishedQuestStep(QuestStepID)`, `static CheckStep(QuestStepID)`, `AdvanceToNextTrial(bool isMidMapAdvance)`, `SetTrial(int)`, `SolveTrial(int trialIndex, bool isForcedNoDialogue)`, `GiveTrialReward(int chapterIndex, bool showPopUp)`, `OnClaimRewardButtonPressed()`, `StartChapterIntroDialogue()`, `ISaveable_GetStateForSaving(SaveGame)/ISaveable_RestoreSavedState(SaveGame)`.
- `Trial` [V]: `idForSaving, List<ProgressionTask> tasks, hasBeenFinished, suspended, Evaluate()`. `ProgressionTask` [V]: `Func<bool> checkIfSolved`, `Action OnSolved`, `_isComplete`, `Evaluate()`. Tasks are polled predicates over local state (inventory counts via `OwnedAmount(Item)`, POIs solved, people helped) [I].
- `QuestStepID` enum [V] (550, 1010–1090, 2010–2020, 3010–3020) and `GameEvents/Progression.OnFinishedQuestStep(QuestStepID)`, plus `OnTrialCompleted(int)`, `OnTrialIDCompleted(string)`, `TriggerStartNextTrial`, `OnTrialConcluded`.
- `CurrentChapterInfo : ScriptableObject` [V]: `currentTrialNumber, currentTrial, currentTrialIsSuspended, mainQuestMapsToSpawnNext`.
- Generated side quests, POI-local [V]: `QuestGenerator` (`questSteps` with `dialogues`, `journalLogKey`, `requiredForCompletion`), `QuestPageHelper`, `POI_Logic_RequestItems`, `ItemRequester`, `SickNPC/SickAnimal`, `FoodVillagerLogic`. Their state is POI elements in the map save.
- POI element state machine [V]: `CompletableStation.isCompleted` with `SetIsCompleted(bool)`. `GenericPOICondition : CompletableStation` has `SetConditionTrue()/SetConditionFalse()`, `trueHasBeenCalled/falseHasBeenCalled`. `POI_Controller : CompletableStation` has `poiConditions` and `onAllConditionsFulfilled`. Each element has `UniquePoiElementID` plus its POI's `uniquePoiID`. Saved as `SaveableMap.completableStations` and `poiElementStates` (`SerializablePoiElementState{flags, ints, values, points, texts, disabled, destroyed}`). Fresh map: 457 stations [V].
- Global flag stores, string-keyed [V]:
  - `StaticGameCondition.static Dictionary<string,bool> syncedConditions`, `static SetCondition(string,bool)`, `static GetCondition(string)`.
  - `StaticGameValue.static Dictionary<string,int> syncedValues`, `SetValue(int)`, `static GetValue(string)`, `static Action<string> onChanged`.
  - Both persist into `SaveableProgress.staticGameInformation` (Progress.xml: `Learn_HelpGhosts`, `BabaJagaSleeps`, `CanLightFire` [V data]).
  - `StaticMapCondition.static Dictionary<string,bool> syncedConditions`, `static ChangeCondition(string,bool)`, persisted per map (`SaveableMap.staticMapInformation`).
  - Change event: `GameEvents/SaveSystem.OnStaticGameConditionChanged(string)`.
- Saves [V]: Progress.xml = `SaveGame/SaveableProgress` (completedChapterIDs, completedTaskIDs, completedQuestSteps, currentTrialIndex, claimedChapterRewards, knownItems/Recipes, bbsThatWereCommentedOnByNPC, staticGameInformation, blueprintUnlock*). Guidance.xml = `SaveablePlayerGuidance`, which is only "already showed popup X" bools (`PlayerGuidance.alreadyUsedPopUp_*`). Stats.xml = `StatsTracker` (playTime, collectibles, POIs visited/solved, bbsPlaced...). Collection.xml = foundTrinkets/foundWearables.
- **CurrentMap_c.xml is gzip-compressed XML** [V: magic 1f 8b, decompresses to 768 KB]. This answers the NOTES "save parts compressed?" unknown for the map part. Other parts are plain XML.

## 2. Deterministic vs must-sync
| State | Verdict |
|---|---|
| Which NPCs and POIs exist, villager looks, animal herd spawn | Comes from the shared save/map (SaveableMap.pois, characterBodyDefinitionIndex, HerdRandom(pos)). Same if both load the same map [I, depends on world-gen slice]. |
| NPC position and activity | **Must sync** (or accept drift). `GenerateNextActivity` and wander picks are random, and navmesh is baked at runtime per peer [I]. Drift is visible because players watch NPCs together. |
| NPC talking pose / look-at | Sync a "talking to player N" flag. Look-at uses the static `PlayerTransform` (local only) [V]. |
| Dialogue text, line index | Deterministic given term and index. Sync only the start, choice and end events. |
| Rumour pool, heard rumours, heardDialogueIDs | Must sync (host authoritative). They change what every NPC offers. |
| POI station/condition state, StandaloneDialogue.hasBeenActivated | Must sync. This is the quest progress. |
| Static game/map conditions and values | Must sync (small, string-keyed). Easiest win. |
| TasksTracker trial/chapter index, completedSteps, claimed rewards | Must sync, host owns. Clients must not run `Evaluate()` side effects (rewards, popups, map changes) on their own [I]. |
| Guidance popups | Per player and cosmetic. Do not sync. |
| Stats, Collection | Per player. See guest progress. |

## 3. Proposed sync design (fits CoopSession)
Authority: the host owns NPC simulation, the rumour pool, flags, POI state and TasksTracker. Clients run NPC AI in "puppet" mode, as with PlayerSync. Client actions go to the host as requests. The host applies them and relays the results. Messages (next free ids):

| Id | Name | Fields | Direction / rate / delivery |
|---|---|---|---|
| 18 | NpcState | count, then per NPC: `int key` (hash of uniquePoiID+uniquePoiElementID), pos (3×f16 or f32), yaw (f16), `short behaviourIdx`, `byte flags` (talking, frozen, carried), `short anim state` | host→all, 5 Hz, only NPCs within ~80 m of any player, Sequenced. Clients interpolate 200 ms. |
| 19 | NpcBehaviour | key, `string behaviourName`, targetPos, activityPhase | host→all on `SwitchToBehaviour` postfix, ReliableOrdered (corrects pose and anim exactly) |
| 20 | FlagSet | `byte kind` (GameCond, GameValue, MapCond, Heard), `string id`, `int value` | either way. Client→host as a request, host applies and relays. ReliableOrdered. Full table on join. |
| 21 | PoiElementState | uniquePoiID, elementID, `byte op` (SetCompleted, CondTrue, CondFalse, StandaloneActivated, DecisionOpt1/2), `bool` | client→host request, host→all. ReliableOrdered |
| 22 | DialogueEvent | `int npcKey`, `int playerId`, `byte op` (Begin, RumorStart idx/term, Line idx, Choice n, End, Locked) | ReliableOrdered. Host arbitrates the lock. |
| 23 | QuestState | trialIndex, ChapterState, ChapterTag, completedSteps[], claimedRewards[] | host→all on change and on join, ReliableOrdered |
| 24 | RumorDelta | op Add/Redact, rumour localization term | host→all, ReliableOrdered |

Harmony hook points (all signatures verified; whether a patch works on IL2CPP needs a runtime test):
- NPC: on clients, prefix `GeneralAIController.TickUpdate()` / `TickBehaviourTransitions` → return false, and drive the transform and animator from NpcState (or `FreezeAI()` plus set agent `updatePosition=false`). Host postfix `SwitchToBehaviour(AIBehaviour,bool)` → NpcBehaviour. Clients prefix `VillagerBehaviour.GenerateNextActivity(bool,VillageActivityplace,bool)` → false.
- Flags: postfix `StaticGameCondition.SetCondition(string,bool)` (static), `StaticGameValue.SetValue(int)`, `StaticMapCondition.ChangeCondition(string,bool)`, `SaveLoad_HeardDialoguePerMap.SetHeard(string,bool)`. Add a re-entrancy guard (`_applyingRemote`) so applying a remote value does not echo it back.
- POI: postfix `CompletableStation.SetIsCompleted(bool)`, `GenericPOICondition.SetConditionTrue/False()`, `StandaloneDialogue.StartDialogue()`, `POI_Decision.TriggerOption1/2()`. Resolve the remote target by walking `POI_SaveLoad`/uniquePoiID → element with matching `UniquePoiElementID`.
- Dialogue: prefix `NPC_DialogueManager.PresentsDialogueOptions(List,bool)` and `StandaloneDialogue.StartDialogue()` for the lock check. Postfix `StartRumor(Rumor)`, `SayGoodbye()`, `ImmediateGoodbye()`, and `DialogueFunctions.EndDialogue()` for begin/choice/end. Subscribe to `GameEvents/UI/PopUps.OnDialogueOpened/Closed` for UI-level state.
- Quests: on the host, postfix `TasksTracker.OnFinishedQuestStep`, `AdvanceToNextTrial(bool)`, `SolveTrial(int,bool)`, `GiveTrialReward(int,bool)` → QuestState. On clients, prefix `TasksTracker.Evaluate()` → false (host-driven), then apply with `SetTrial(int)` and copy statics.
- Rumours: postfix `RumorCollection.Add(Rumor)` / `RedactRumor(Rumor)` (host) → RumorDelta.

### Open decision: dialogue ownership
- **A. Host-only talks, others watch.** Simplest. Clients send nothing. Each client is shown the host's line text in its own `DialogueUILogic` as passive lines (`isPassive=true`, no camera, no input lock). Cost: guests can never talk to anyone, which is bad for a cozy game with many villagers.
- **B. First talker locks the NPC (recommended).** The client requests DialogueEvent Begin and the host grants or denies (the host is the arbiter even for its own talks). While locked, other players get `NPC_DialogueManager.SetNPCCanNotTalk(...)` locally, which plays the existing "can't talk" path [V method]. The talker runs the real dialogue locally with the full camera. Consequences (rumours heard, POI conditions, decisions, items given) flow through the flag, POI and rumour messages. Bystanders near the NPC see the NPC's talk anim via NpcState flags, and optionally passive subtitles. Matches the host-authoritative model. The risk is UnityEvent side effects that call non-hooked code locally on the talker only.
- **C. Each player has own dialogue, shared quest flags.** No locks: NPCs can talk to two players at once. The NPC body can only face one player (`BeginTalk`, static `PlayerTransform`), and rumour redaction races (both hear a "priority" rumour, both get the loot). Needs dedupe on the host for every reward. Most freedom, most bugs.

### Open decision: guest progress (what a guest keeps after leaving)
Facts: quest, chapter and POI state belongs to the host's world. Stats and Collection are already separate save parts, and Character is already a per-player profile.
- **Nothing.** The guest's own save is untouched. Easiest. Avoids loading host chapter state into a guest who is earlier or later in the story.
- **Stats only.** Merge `StatsTracker` counters (playTime, POIs visited/solved, collectibles) into the guest's own Stats.xml on leave. Low risk: the counters only feed achievements and stat UI [I].
- **Collection (recommended with stats).** Union `foundTrinkets/foundWearables` into the guest's Collection.xml. These are cosmetic unlocks. They pair with the appearance profile and cannot break the guest's story. Never copy Progress.xml (chapter, trial, staticGameInformation): it would skip or break the guest's story. Write via a mod-side sidecar (like appearance.xml), not by loading the guest's save mid-session.

## 4. Risks and runtime-test questions
1. Does a Harmony prefix on `GeneralAIController.TickUpdate` / `Update` stop movement, or does NavMeshAgent still move the body (`agent.updatePosition`)? Root motion via `OnAnimatorMove` [V] may still move it.
2. Are `uniquePoiID`/`uniquePoiElementID` identical on two peers that load the same save? (They should be, because they are serialized [V data].) Do runtime-spawned NPCs (WildAnimalGroup herds, crows) have ids at all, or do they need a spawn-order key?
3. Does dialogue set `Time.timeScale` or only GameMode InGameUI? Does the pause menu set timeScale=0 on the host? If so, the mod must block or neutralise host pause (prefix `PauseMenu.OpenPauseMenu` → show the menu without freezing).
4. Static `GeneralAIController.PlayerTransform`: what happens to NPC look-at, flee (crows, goats) and "blocked by player" when a puppet stands in the path? Puppets are not obstacles, so NPCs walk through guests [I].
5. AI LOD (`SetLODLevel`, `ActivitySuspensionReason.LODMinimal`) is driven by local player distance. Host NPCs far from the host but next to a guest may be frozen at Minimal LOD. The host must treat every player as an LOD source.
6. `TasksTracker.Evaluate` reads local inventory (`OwnedAmount(Item)`). Does item-delivery quest progress need the host to see the guest's inventory? Inventory slice dependency.
7. Rumour identity on the wire: Rumor has no id field [V]. Key by `rumor` LocalizedString term plus owning NPC key. Need to check terms are unique.
8. UnityEvent chains on POI prefabs (`onFulfilled`, `OnOption1`, `onRumorHeard`) run arbitrary methods. Every un-hooked side effect (loot spawn, teleport, map preset change) desyncs. Test with the tutorial POIs (HangingMan, AbandonedMirror, PumpkinLady) that appear in the fresh map.
9. `TutorialNPCLogic` (Baba Yaga) teleports when unseen by the local camera. It is host-only logic, so the guest may see her vanish.
10. `TasksTracker.ShowChapterPopUp` / title cards / `PauseForbidReason.PopUp`: does a guest get the chapter-start popup, and does it lock the guest's input?
11. `DialogueUILogic.AutoAdvanceForTesting` (static bool) [V]: confirm it works so bot instances can run dialogue unattended.

## 5. First implementation step (smallest useful)
**Flag sync (msg 20 FlagSet), host-authoritative, no NPC movement yet.**
1. `Sync/FlagSync.cs`: postfix the static `StaticGameCondition.SetCondition(string,bool)`, `StaticGameValue.SetValue(int)` (read `synchronizationId`), `StaticMapCondition.ChangeCondition(string,bool)`, and `SaveLoad_HeardDialoguePerMap.SetHeard(string,bool)`. Client calls → send to the host. The host applies and relays to the others (exceptPlayerId).
2. On `PlayerJoined` the host dumps all four static dictionaries (`syncedConditions`, `syncedValues`, `heardDialogueIDs`) to the new client.
3. Re-entrancy guard and log every flag change with its id. Running the tutorial with two copies then gives a list of the real flag ids and shows which progress lives in these stores and which lives in POI elements. That data decides the shape of msgs 21–23.
4. Verify: the client hears a dialogue (heardDialogueIDs grows) and the host log shows the same term. Bump `Protocol.Version` to 4.
