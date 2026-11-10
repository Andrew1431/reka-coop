# Story machinery survey (Reka 1.0.32, host-first plan)

Source: interop stubs (`acs-1032`, signatures and fields only; no bodies) plus the mod as of protocol 57. Every
"reads X" or "calls Y" below is inferred from fields, method names and parameter types. [G] marks a judgement that is
a guess. Columns: **Trigger** (I = player interact, W = player walks in, F = per-frame or polled evaluation,
C = callback from another type or UnityEvent, T = timer, L = load or enable), **State** (S = in the save:
`gs` global ISaveable, `map` ISaveableMapElement, `poi` ISaveablePOIState/ISaveablePOIElement element state,
`tut` ITutorialSaveStatePart, `-` not saved), **Player** (how it finds the player).

Buckets: A covered (mechanism named), B guest->host intent, C silence on guest at the source, D host->guest one-off
or state mirror, E local/cosmetic, ? probe. A row can name several; the first letter is the one it is counted under.

Existing mechanisms, as used below: **TS** TutorialSync (TutTask.Solve prefix, TutorialSolve intent, block stepping,
TutorialDialoguePiece.RunDialogue blocked), **AS** AreaSync (PlayerTrigger story/local listener split, AreaEnter/Exit),
**DS** DetectorSync (ObjectDetector Player tag: guest blocked, host ticked with puppet positions, Enter intent),
**SI** StoryInteract (PlayerDetection whose persistent OnTryToInteract targets CollectibleJournal,
Poi_Tutorial_TeleportTalisman, GivePlayerItemsHelper, StandaloneDialogue -> Interact intent), **DLG** DialogueSync
(Call intents Say/Options/Standalone/Tutorial/PoiDialogue/TakeItem/BabaTalk, DefaultTalk, Pick/Next),
**AP** ActingPlayer (TransformReference PlayerTransformReference/PlayerHead swapped to nearest puppet during
GeneralAIController updates and during intents), **TRS** TrialSync (tracker flattened host->guest; Gain and Stats
guest->host; AdvanceToNextTrial/SolveTrial/SkipCurrentTrial blocked on guest), **RS** RewardSync (GiveTrialReward,
POI_Loot.GiveItemsToPlayer blocked on guest), **PS** PoiSync (POI element states both ways, UseItemInteractArea
fill-up merge), **CS** ConditionSync (GenericPOICondition both ways; Tutorial_FenceGateLock), **SS** StorySync (four
flag tables both ways), **SVS** SaveableSync (ISaveable list host->guest, UseItem_* both ways), **ACT** ActiveSync
(on/off of story objects host->guest), **POP** PopupSync, **AI** AiSync, **HUT** HutSync, **WO** WorldObjects
(pickups/destruction, first come), **CAR** CarrySync, **WT** WorldTransfer.

## 1. Quest system (generated side quests, `QuestRunner` under a POI)

Shape: `QuestRunner` (MonoBehaviour, `Update`, `StartQuest`, `ReportDone(QuestTask)`, saves its `QuestProgress` as a
POI element via `CaptureState/ApplySavedState`, static `ChangeAnnounced`) owns `List<QuestStep>`, each a list of
`QuestTask`. `QuestTask` base: `Complete()`, `Bind(QuestRunner)`, `ApplyState(TaskState,bool)` ->
abstract `OnStateApplied`; UnityEvents `onLive`, `onDone`. **No base Update**: each subclass has its own.
Interfaces: `IQuestTargetTask` (CanTarget/AddTarget/Targets: DestroyObjects, LeadSomeone, ReadNote, SendCastMember,
UseItemQuestTask family, WaitForPoiCondition), `IQuestRequestsItems` (GatherItems), `IQuestGivesRewards`,
`ISaveablePOIState` (QuestRunner, UseItemQuestTask, WaitQuestTask, DestroyObjectsQuestTask save their own state).
Player: `QuestAi.get_Player` (static Transform) [G: reads GeneralAIController.PlayerTransform, i.e. the
TransformReference AP swaps], `TalkQuestTask.PlayerWithin(static Vector3,float)`, `MonologueQuestTask.PlayerInArea()`.

Common fix for the whole family (counted once, row "QuestTask base"): guest prefixes `QuestTask.Complete` and
`QuestRunner.ReportDone/StartQuest/Update` -> false; the host's QuestRunner state reaches guests through PS.

| Type | Base | Trigger | State | Player | Bucket / note |
|---|---|---|---|---|---|
| QuestTask (base) | MonoBehaviour | C (Complete) | via runner (poi) | - | C: prefix `Complete` (covers completion of all 35 subclasses) |
| QuestRunner | MonoBehaviour | F `Update`, C `StartQuest` (startOnSpawn) | poi | - | C: prefix `Update`, `StartQuest`, `ReportDone`; D: PS carries `CaptureState` [?: probe that runners are under a POI_SaveLoad so PS sees them] |
| QuestProgress / QuestStep / QuestReleaseSnapshot | Object | - | poi (inside runner) | - | A (PS, with runner) |
| QuestStagingTask (base of 6) | QuestTask | F `Update` -> TickLive/TickDone/TickShow/TickRestage | via runner | AI via QuestCast | C: prefix `QuestStagingTask.Update` (covers Lead/NpcBehaviour/NpcFaceReka/NpcLeave/NpcTeleport/SendCastMember); AI moves come by AI |
| LeadSomeoneQuestTask | QuestStagingTask | F (AI near spot) | via runner | `AiFollowsReka` -> AI follows player | B/AP: follow target is the AI's player ref; AP covers if the follower AI's update reads the swapped ref [?] |
| NpcBehaviourQuestTask | QuestStagingTask | F | via runner | - | A (C above) |
| NpcFaceRekaQuestTask | QuestStagingTask | F | via runner | AI faces "Reka" | A (C above) +AP [?: faces host only] |
| NpcLeaveQuestTask / NpcTeleportQuestTask / SendCastMemberQuestTask | QuestStagingTask | F | via runner | `BothHiddenFromCamera` uses host camera | A (C above); E for vanish-when-unseen (host camera decides) |
| UseItemQuestTask (base of 8) | QuestTask | F `Update` polls `IsDoneInWorld` per target | poi (own) | - | C: prefix `UseItemQuestTask.Update`; the use itself is B (new UseItem intent, see sect. 3) |
| CleanseWithPineBranchQuestTask | UseItemQuestTask | F (UseItemInteractArea solved) | poi | - | B UseItem (M8); completion via base C |
| RevealWithLanternQuestTask | UseItemQuestTask | F (UseItemInteract solved) | poi | - | B UseItem (M8); completion via base C |
| WaterWithWateringCanQuestTask | UseItemQuestTask | F | poi | - | B UseItem (M8); completion via base C |
| BirdsCollectQuestTask | UseItemQuestTask | F (CollectibleID collected) | poi | - | A (WO repeats pickup on host) + base C |
| BirdsDestroyQuestTask | UseItemQuestTask | F (DestructibleObject destroyed) | poi | - | A (WO repeats damage on host) + base C |
| UseItemFireQuestTask / LightWithFire / PutOutWithFire | UseItemQuestTask | F (Lightable lit) | poi | - | B UseItem (M8) for light/extinguish; completion via base C |
| DestroyObjectsQuestTask | QuestTask | F `Update` -> RecordBreaks | poi (own) | - | C prefix `Update`; breaks A (WO) |
| GatherItemsQuestTask | QuestTask | F poll + Subscribe inventory | via runner | shared pack | C prefix `Update`/`OnInventoryChanged`; pack is shared (A) |
| HandInItemsQuestTask | QuestTask | I (`_detection` OnHandInPressed) | via runner | PlayerDetection | B Interact (extend SI to quest-owned detections, M2) |
| InteractQuestTask | QuestTask | I (`_detection`, runtime listener via Listen) | via runner | PlayerDetection | B Interact (M2: SI misses it, the listener is runtime not persistent) |
| ReadNoteQuestTask | QuestTask | I (`_detection`) + C `Readable.onCompleted` | via runner | PlayerDetection | B Interact (M2); reading UI stays local, the task completes on host when the intent runs [?: Readable opens UI on host] |
| PlaceItemQuestTask / PlaceCursedObjectQuestTask | QuestTask | F `Update`/`LateUpdate` (PlaceableObject placed) | via runner | PlaceableObject prompt | B Interact (PlaceableObject prompt, M2) + C prefix Update |
| CarryToPlaceQuestTask | QuestTask | I (OnPickUpPressed/OnPlacePressed) + F rests-on-target | via runner | 2 PlayerDetections | B Interact (M2) + CAR for the carry itself; ? which copy's carriable rests on target |
| LearnSkillQuestTask | QuestTask | F poll `POI_LearnableSkill` / condition | via runner | - | C prefix Update; skill = SS flag |
| MonologueQuestTask | QuestTask | F `Update` -> PlayerInArea -> TryShow | via runner | position (area radius) | C prefix Update on guest; host needs any-player area (M4) |
| ReachAreaQuestTask | QuestTask | F `Update` -> Contains(pos) | via runner | QuestAi.Player [G] | C on guest + M4 on host |
| TalkQuestTask | QuestTask | F `Update` (attach to NPC, TryStartAuto if PlayerWithin autoRadius) + C OnConversationHeard | via runner (+ heard lines in SS) | static PlayerWithin | C prefix Update on guest; talks themselves A (DLG); auto-talk needs M4 |
| QuestConversation / QuestConversationRun | Object | C (from TalkQuestTask) | - (in memory) | NPC_DialogueManager | A (DLG runs it on host) |
| RewardQuestTask | QuestTask | C on live (`CompletesWhenLive`, Give) | via runner | POI_Loot | A (RS blocks guest POI_Loot) + base C |
| WaitQuestTask | QuestTask | T `Update` elapsed | poi (own) | - | C prefix Update |
| WaitForTimeOrWeatherQuestTask | QuestTask | F (WeatherManager hour/weather) | via runner | - | C prefix Update (weather is host's) |
| WaitForPoiConditionQuestTask | QuestTask | F (CompletableStation) | via runner | - | C prefix Update |
| AnimateObjectQuestTask | QuestTask | F `Update` + C OnAnimationEnded (POI_AnimateObject) | via runner | - | C prefix Update; POI_AnimateObject pose saved (poi, PS) |
| BringTheHutQuestTask | QuestTask | F (hut near, seated) every CHECK_INTERVAL | via runner | hut TransformReference | C prefix Update (hut is host's, HUT) |
| WorldChangeQuestTask | QuestTask | C on live -> StartEffects, F TickEffects | via runner | - | D: effects play on guest when PS applies the state (OnStateApplied) [?: does ApplySavedState replay or snap]; C prefix Update if it double-plays |
| QuestEffect + 10 subclasses (FadeVfx, Grow, HideWithDust, Material, Music, PlayVfx, Sound, Weather; Library/Vfx/SpawnedVfx are data/helpers) | Object | C from WorldChangeQuestTask.Begin/Tick, `PlayedOnce` static event | -, `SnapToEnd` on load | - | E (visual) except QuestEffectWeather -> weather (host's, A) and QuestEffectMusic (local E). PlaysOnce effects (sound/vfx/dust) need D one-off if guests should see them |
| QuestTaskStepEventForwarder | MonoBehaviour | C `Advance()` (UnityEvent) -> TasksTracker.OnFinishedQuestStep | gs (completedSteps) | - | C prefix `Advance` on guest (TRS mirrors steps) |
| QuestCast | MonoBehaviour | - (resolves members) | - | - | E (lookup) |
| QuestAi | static helper | - | - | `get_Player` static | ? probe: does `get_Player` follow AP's swapped TransformReference |
| QuestGenerator / QuestPageHelper / QuestPageHelperTextAdder / QuestPageUi / QuestElement* / QuestActivityLog | various | C | QuestPageHelper is CompletableActiveStateObserver | - | E (journal UI; follows ACT/PS) |

## 2. Main story: TasksTracker, trials, chapters

| Type | Trigger | State | Player | Bucket / note |
|---|---|---|---|---|
| TasksTracker | F `Update` -> `Evaluate`/`EvaluateInFiveFrames`; C GameEvents.Tutorial.Ritual_* handlers (OnPlacedChicken... OnRitualFinished), `OnFinishedQuestStep`, `OnSkippedTutorial`, `OnTrialTitleCartDismissed`, `StartChapterIntroDialogue` | gs (trial index, done tasks, steps, tut counters, claimed rewards, ritual progress) | `OwnedAmount` = shared pack; StatsTracker (local counts) | A (TRS) now; C next: prefix `Update`, `Evaluate`, `EvaluateInFiveFrames`, `OnFinishedQuestStep`, the Ritual_* handlers, `OnTrialTitleCartDismissed`, `StartChapterIntroDialogue`, `ShowChapterPopUp` on guest, then drop TRS Gain |
| Trial | C from TasksTracker.Evaluate | gs (via tracker) | - | A (TRS) |
| ProgressionTask | F `Evaluate` (Func<bool> checkIfSolved, Action OnSolved) | gs (via tracker) | predicates read stats/pack | A (TRS); covered by tracker C |
| StatsTracker | C GameEvents (OnCollectibleCollected, OnPOIVisited/Solved, OnPlaceBB, OnTrade, OnTeleport, people helped) | gs | local player's actions only | A (TRS Stats deltas); later B: count a guest's pickup when WO repeats it on host (fold Stats into the Collect path) |
| CurrentChapterInfo | C `SyncCurrentChapterMirrors` | ScriptableObject, derived | - | A (follows tracker) |
| ChapterState / ChapterTag (statics in TasksTracker) | C | derived from gs | - | A (TRS) |
| ChapterPoiPlan / ChapterGenerationSettings / ChapterVisuals | data | - | - | E (map generation is host-only, WT) |
| ChapterBBReward / ChapterUseItemReward / rewardsPerTrial | data | - | - | A (RS; GiveTrialReward blocked on guest) |
| PopUp_Chapter | C QueuePopUp; `onConfirm` UnityEvent | - | - | A (POP shows host's); C: a guest's Confirm must not reach TasksTracker.OnTrialTitleCartDismissed (see above) [?: which listener onConfirm calls] |
| TrialLocation | C static `StartNext`, `CancelTrial`, `ConcludeTrial` | - | - | C prefix the three on guest [G: scene-wired] |
| BeyondEdgeOfMapHouseTravelTrigger | F `Update` (hut past radius) -> Trigger popup -> Confirm | - | home Transform | C prefix `Update` on guest (hut is host's); travel A (WT) |
| MapBorderFogManager | F `LateUpdate` DoPlayerCheck/DoHomeCheck -> TriggerTravel | - | TransformReference | C `TriggerTravel` on guest; player clamp E |
| PoiMapHistoryTracker | C OnEnteredMap, RecordVisit/Completion | gs | - | A (SVS) |
| PoiQuestInterface | F `Update` CheckPlayerProximity -> VisitPoi; C StartQuest/SolvePoi/FailPoi | poi | position [G: TransformReference] | C prefix `CheckPlayerProximity`/`SolvePoi`/`StartQuest` on guest; host needs M4 so a guest's visit counts; A (PS) for state |
| StaticGameCondition / StaticGameValue / StaticMapCondition | C setters; `onConditionTurnedTrue` etc. | gs / gs / map | - | A (SS) now; phase 4: drop guest->host half |
| StaticGameConditionChecker / StaticGameConditionEvents | C GameEvents.SaveSystem.OnStaticGameConditionChanged | - | - | D: fires on guest when SS applies a flag (that is how guest-side presentation follows); C if any listener is story [?: list its targets] |

## 3. Physics triggers and detectors

| Type | Trigger | State | Player | Bucket / note |
|---|---|---|---|---|
| PlayerTrigger | W OnTriggerEnter/Exit, CheckForPlayer, FireHeldExit | - | `playerCols` (local colliders) | A (AS) |
| ObjectDetector (Player tag) | W ProximityTick(playerPos) / OnTriggerEnter; OnEnter/OnExit | poi | tagString "Player", position from ProximityDetectorManager | A (DS) |
| ProximityDetectorManager | F `Update` ticks registered detectors | - | `get_PlayerTransform` (TransformReference) | A (DS hooks ProximityTick) |
| MultipleObjectDetector | F ProximityTick / OnTrigger (objects, not player) | poi | objectsToDetect | C prefix `FireAmountEvents` on guest; host's objects move by CAR (A) [?: carried object's host copy position is close enough to fire] |
| POI_AIDetector | W (AI) OnTriggerEnter/Exit + F ReevaluateColliders | poi | AISettings | C prefix `OnTriggerEnter`/`Update` on guest (AIs are host's, AI) |
| POI_PlayerAreaTrigger | W OnTriggerEnter/Exit -> OnEnter/OnLeave; I TryToInteract | poi | `howManyCollidersOfMineIsPlayerIn` | B AreaEnter/AreaExit (extend AS, M7) + I via Interact (M2) |
| POI_Ritual_PlayerAreaTrigger | W + I (`Interact`) | - (CompletableStation flag in map) | same | B (M7 + M2) |
| CombinedTriggerElement / CombinedTrigger | W OnTriggerEnter/Exit -> `OnCombinedTriggerEnter/Exit` UnityEvents (FixedUpdate) | - | any collider | B (M7; split story/local listeners like AS) |
| SlowZone | W -> GameEvents.Player.OnEnterSlowZone + OnEnter/OnExit | - | playerInside | E (movement is per player); B only if OnEnter has story listeners [?] |
| BirdsStartFlyingTrigger | W OnTriggerEnter -> StartFlying, OnStartFlying | poi (STATE_FLOWN) | collider [G: player] | B (M7) + C prefix on guest; birds' flight D via PS state |
| TriggerDayTimeChanger | W -> WeatherManager daytime | - | instancesInTrigger | C on guest (weather/time is host's) + B (M7) so a guest's entry changes the host's time [?: story or cosmetic] |
| TeleportPlayerHomeOnTriggerEnter | W -> PortToSurface | - | LayerMask player | E (rescues the local player; per player) |
| VillageDoor | W + F `Update` (inArea count) | - | inArea transforms | E (door swing; puppets don't open it on others, cosmetic) |
| VillageMusicParameterSetter / VillageEnterLeaveGameEventCaller | W -> music param / GameEvents.ExplorePhase.OnEnterVillage | - | collider | E (music); ? whether OnEnterVillage listeners count visits (StatsTracker) |
| PropertyHouse | W OnTriggerEnter/Exit (villagers/player), ActivateRitualInHouse | - | collider | ? probe what enters (villager or player) |
| CollectibleDetector / ObjectDetection / TreeRotator / LogPlayerComingAndGoing / LogCollisions / BBPlacementCollisionChecker | W | - | local | E |
| Tutorial_RitualSiteTrigger | C GameEvents.Player.OnStart/StopCarryingObject (what is carried) | - | local player's carry events | B: a guest's carry never raises the event on host (M5) |
| PlayerDetection (POI_Logic) | I TryToInteract / SimulateInteraction -> OnTryToInteract | completable station flags | interact system, `interactionLegalAfterTimestamp` static | A (SI) for persistent story targets; B for runtime-listened ones (M2) |

PlayerDetection users: BuyableItem, BuyableLoot, BuyingOptionHudLink (E/A: shared pack, first come);
CarriableObject (A CAR); ChickenLegsInteract (A HUT); PlayerSittingPlace (E); POI_Dialogue (A DLG Call.PoiDialogue);
Poi_Tutorial_TeleportTalisman (A SI); POI_Demo_CrookedPineBranch (A SI via GivePlayerItemsHelper [G]);
POI_Ritual, POI_Ritual_SupplyToPlace, PlaceableObject, InteractQuestTask, HandInItemsQuestTask, ReadNoteQuestTask,
PlaceItemQuestTask, CarryToPlaceQuestTask (B M2). ObjectDetector code users: PetEligibility, POI_AIFunctions and
GenericPOIFunctions (`GetAIFromObjectDetector`); all other uses are scene-wired.

## 4. POI_* / Poi* (116 types)

Families with no story state (counted as one E row each): spawn placement (POISpawn_* x14, PoISpawnBehaviour,
POI_HouseRitualSpot*, POI_HouseInformationHolder, PoiLayoutSolver, PoiSlot*, PoiMapFill, PoiPathGround, PoiPickReport,
PoiStateSpace, PoiChickenLegsNavBlocker, PoissonSampler); data and catalogues (POICollection, POIForceSpawnScrob,
POI_SpawnPreset, POI_QuestCollection, PoiCatalog, PoiGrassData, PoiTypeReference, PoiSlotTypeColors,
PoiSolvedMusicTable, POIElementIDData, POIInfo, PoiIdAttribute, PoiActivityPool, PoiBBReward, PoiUseItemReward,
POIAlignment); presentation (POI_MaterialModifier, POI_MultiVFXController, POI_AnimatorInteractions,
POI_RigidBodyManipulator, POISoundTriggerer, POISoundAlignmentUpdater_Daytime, POI_PlayMusic,
POI_PumpkinLady_AnimationController, POI_VolumeActivator, POI_Overview, POI_CantTakeOffMaskIfEnabled,
POI_SaveLoadSetActiveOrder, POITestRunnerBehaviour, POI_SpecificItemValueAssigner, POI_DialogueSupportFunctions).

Ones that hold story state or fire events:

| Type | Family | Trigger | State | Player | Bucket / note |
|---|---|---|---|---|---|
| POI_SaveLoad | save | L/save | map | - | A (PS uses it) |
| POI_StateSaver | save | F `Update` | transforms/actives | - | A (PS/ACT) [?: what Update does] |
| POI_States | logic | C SwitchState -> OnEnter/OnLeave/OnRebuild | poi | - | C prefix `SwitchState` on guest, D via PS (ApplySavedState -> SimulateTimelineHavingPlayed) |
| POI_Controller | logic | F `Update` over poiConditions -> onAllConditionsFulfilled | poi | - | C prefix `Update` on guest (conditions arrive by CS/PS and would fire it again) |
| GenericPOICondition | logic | C SetConditionTrue/False -> onFulfilled | poi | - | A (CS) now; phase 4: guest stops originating (its sources become intents) |
| CompletableStation | logic base (26 subclasses) | C SetIsCompleted; OnLoadedAsCompleted events | poi | - | A (PS) |
| POI_Event | event | C `Trigger` (cooldown, once) -> eventToTrigger | poi (triggered) | - | C prefix `Trigger` on guest (PLAN already lists it) |
| POI_InvokeEventAfterTime | event | T `InvokeEventAfterTime` | completable | - | C prefix `Invoke` on guest |
| POI_EventAfterChapterIntroDialogue | event | C GameEvents.Progression.OnTrialIntroDialogueFinished | - | - | C on guest (`InvokeEvent`) |
| POI_EventAfterTrialTitleCardDismissed | event | C GameEvents.Progression.OnTrialTitleCardDismissed | - | - | C on guest; a guest dismissing the shown card must not count |
| POI_EventAfterEnvironmentGenCompleted | event | L GameEvents.Environment.OnGenerationFinished | - | - | E [?] deterministic setup on every copy; probe its targets |
| POI_EventOnActivatedGO | event | L OnEnable/OnDisable -> onActivate/onDeactivate | - | - | C/? fires on guest when ACT switches the object on; probe targets |
| POI_EventOnNotVisible | event | F `Update` hidden from camera -> OnNotVisible | - | camera (local) | C on guest (host camera decides) |
| POI_MultiObjectEvent / POI_MultiObjectEventManager | event | C TriggerEvents | - | - | C prefix `TriggerEvents` on guest [G] |
| POI_MultipleCounters | event | C CountUp/CountDown -> TriggerHighest/Lowest | - [G] | - | C on guest; count is unsaved [?] |
| POI_MultiConditionLerp | event | F `LateUpdate` over POI_Controller -> onConditionAmountFulfilled | - | - | C prefix `LateUpdate` on guest |
| POI_MultiRitual | event | F `Update` -> OnFinishedRituals | - | - | C prefix `Update` on guest |
| POI_Monologue / POI_Monologue_Random | dialogue | F `Update`, C StartMonologue (from detector) | completable | - | A (DLG passive lines, DS); C prefix `Update` on guest [?: Update starts lines] |
| MultiPassiveDialogue | dialogue | F `Update` -> StartPassiveDialogue | completable | - | C on guest + D (DLG passive lines already shown on guests) |
| POI_Dialogue | dialogue | I TriggerInteraction (+ThanDisable) | completes a GenericPOICondition | PlayerDetection | A (DLG Call.PoiDialogue/PoiDialogueOnce) |
| POI_DialogueSequence | dialogue | C StartSequence/SequenceEnd | - | - | A via upstream (talk runs on host) |
| POI_DialogueByWeather | dialogue | F `Update` UpdateRumor | rumours | - | C on guest (rumour pool is host's) [?] |
| POI_PersistentCharacterDialogues | dialogue | C SetState | ? | - | ? |
| POI_Decision | decision | C TriggerDecision -> OnOption1/2 | - | - | A (POP: host raises, leader picks) |
| POI_PopUp | popup | C ShowPopUp -> OnPopUpClosed | - | - | A (POP shows host's); C if a guest's close fires story [?] |
| POI_Logic_RequestItems | request items | C OnInventoryOpened/Closed (UI) -> CheckQuestRequirements -> OnSolved | poi | inventory UI of the local player | B new kind HandIn (the guest's UI decides what is given) |
| POI_TakeItems | request items | C TakeItemFromPlayer -> OnItemsTaken | quest page | shared pack | A (DLG Call.TakeItem) when reached from a talk; C otherwise |
| POI_ItemInInventory / POI_InventoryItemValueEvents | request items | C Check -> events | - | shared pack | A by upstream |
| POI_ItemConverter | request items | F `Update` -> onConversion | - | - | C prefix `Update` on guest [?] |
| POI_AddChildrenItemToQuestRequirements | request items | C AddToQuest | quest page | - | E |
| POI_Logic_SacrificialStone | request items | C OnStopInteracting (UI) -> EvaluateInventoryWorth -> SpawnLoot, OnSolved | - | inventory UI | B HandIn |
| POI_Loot | station/reward | C GiveItemsToPlayer etc. | poi | - | A (RS) |
| POI_LearnableSkill | station | C LearnedSkill | SS condition (static `Sgc`) | - | A (SS) |
| POI_Ritual | ritual | I TriggerInteraction -> PerformRitual; F `Update` | poi | PlayerDetection, TransformReference | B Interact (M2); C prefix `Update`/`PerformRitual` on guest; GameEvents.Player.OnStartRitual is local (M5) |
| POI_Ritual_SupplyToPlace | ritual | I TriggerInteraction -> PlaceItem -> onPlaceItem | completable | PlayerDetection | B Interact (M2) |
| POI_Demo_CrookedPineBranch | tutorial pickup | I -> OnPicked | poi (revealed, picked) | PlayerDetection, lantern reveal | A (SI) for pick; reveal B UseItem (M8) [G] |
| Poi_Tutorial_TeleportTalisman | tutorial pickup | I PickUp -> popup, onSolve, StartNext | via conditions | PlayerDetection | A (SI) |
| POI_VillagerAssigner | NPC | F `Update` TryToAssign -> OnAssign | - (POI_VillagerAssigner key) | - | C prefix `Update` on guest; villagers are host's (AI) [?: assignment differs per copy] |
| POI_AIFunctions | NPC | C (UnityEvent targets) SendAIToAnchor, WaitForAIToReachTarget -> onAIReachedTarget | - | TransformReference (player as AI target) | A by upstream + AI; AP for "target the player" |
| POI_PlayerFunctions / POI_PlayerEventSender | player | C PlayerBlockMovement, PlayerHoldItem, SitDown, PlaceItem events | - | local player | E when run for the local player; D: when the host runs one for a guest's story it moves the host [?: needs routing to the acting guest] |
| POI_ReputationAdjustor | reputation | C -> GameEvents.Player.Reputation | ? | - | C on guest [?: where reputation is saved] |
| POI_BBPlacer / POI_BBGetter | reward | C PlaceBB, AddToLoot | poi | - | A (PS) |
| POI_AnimateObject | presentation | C StartAnimation, F `LateUpdate` | poi (pose) | - | A (PS) |
| POI_MaterialController / POI_VFXController | presentation | C lerps | poi | - | A (PS), E |
| POI_AllChildrenDestroyed | logic | C CheckDestructibleObjects -> condition | via condition | - | A (WO destruction + CS) |
| POI_CountAsSolvedVillager | marker | - | - | - | E |
| POI_PoisonVillager | quest | C StartQuest/OnPoisoned/OnFinished/OnFailed | - | PoiQuestInterface | C on guest; A by upstream (FoodVillagerLogic) |
| PoiQuestInterface | quest | see sect. 2 | poi | - | C/M4 (sect. 2) |

Other station-like story types (non-POI_ names): CarriableObject (A CAR), PlaceableObject (B M2, poi),
CarriableObjectPlacementHelper `PlaceCarriedObject` -> onPlace (B: placing happens on the carrier's copy; M1),
CollectibleJournal (A SI), TreasureChest (A WO first come), OnlyDoSomethingOnce `TryToDoSomething` (C on guest),
ScaleObjectsOverTime (A PS), LeshyDissolver (C `Update` on guest), DziadyMaskCheck (local mask state -> condition: B
[?]), CountDeerCharmedTowardsLeshyApproval (C, B when a guest charms), UseItemInteract (B M8), UseItemInteract_OverTime
(poi, B M8 start/stop), UseItemInteractArea (poi, F `Update` -> OnAllSolved; A PS fill-up, C `OnAllSolved` path on
guest), Readable (I -> onCompleted: B via M2), DestructibleObject (A WO), CollectibleID (A WO; its OnPluck/OnCollectAction
listeners need M1), FoodVillagerLogic (UI table -> GiveReward: B HandIn), PoI_Logic_Patient (Interactable minigame UI:
B HandIn or E-host-only, ?), SickNPC/SickAnimal (C OnPlayerLeftCorrectItem, B via PlaceableObject), ItemRequester
(I Interact: B M2), LifelessLutkiManager (pickup/place on shrine events; map: A CS for planks, B M1 for its pickup
events), TrapNearbySpirit (F `Update` with bait, TransformReference: C + M4), BoneDissolver (F near player hand,
poi: C on guest + M4), WeatherSensor (F, TransformReference -> condition: C + M4), WeatherManipulator (poi; A weather
sync), Familiar/FamiliarManager (F, requests from player: E per-owner? ? decide who owns familiars), BabaJagaEncounter
(map, C on guest; A AI), LeshyEncounter/RandomLeshyEncounterManager (F camera-visibility: C on guest),
GhostRitualGlide (F, condition: C on guest), RitualCandleIgniter (C from condition: A by upstream), ChickenLayEgg
(F timer egg spawn: C on guest + D [?: egg identity]).

## 5. Tutorial

| Type | Trigger | State | Player | Bucket / note |
|---|---|---|---|---|
| TutorialManager_v2 | C Solve/StartNext*/StartTutBlock; coroutines StartCheck_Walk/Sprint/Look/RaiseLantern/BuiltFloor/RotateBB/OffsetBB | gs + tut (SerializableTutorialTaskProgress) | `Player player` (Rewired), local input | A (TS) |
| TutorialManager_v2.TutTask | C Solve, CheckSolveCondition, startContinousSolveCheck | tut | closures | A (TS) |
| Tutorial_TrackedValues | C GameEvents.Tutorial listeners, Monitor* coroutines | - | local input | E (feeds local tasks; guest's solve goes through TS) |
| TutorialNPCLogic (Baba) | I TriggerInteraction; C SetDialogue/RunDialogue pieces; OnApproachedGate, OnRitualFinished; teleports unseen | map + tut | TransformReference, host camera (IsUnseenByCamera) | A (DLG Call.Tutorial, TS piece block, AI); D: piece WasSet/DidRun not mirrored to guests [?: matters only if the guest becomes host or reloads] |
| TutorialNPCLogic.TutorialDialoguePiece | C RunDialogue | tut | - | A (TS) |
| TutorialEventReceiver | C GameEvents.Tutorial.* (EventStep) -> onStepCompleted | - | - | A by upstream (event fires only where the story runs); D if guests need its presentation [?] |
| RitualProgressionAdvancer | C `Advance()` (RitualStep: PlacedChicken ... OnStartGettingEmbers) -> GameEvents.Tutorial.Ritual_* [G] | - (tracker counters gs) | - | B new: guest prefix `Advance` -> intent (Call entry RitualAdvance), host runs its own. One patch covers every ritual step [G: scene-wired as the ritual's UnityEvent target] |
| DebugTutorialRitualSkipper | dev | - | - | E |
| LanternTutorialLogic | C OnLanternTaken, OnLanternEquipped (GameEvents.Player.UseItems.OnEquip_Lantern), OnRaisedLantern/Lowered, OnWalkedThroughFog | - | local UseItems events | A (TS solves the tasks); the events are the local player's (M5 if the host must react to a guest's lantern) |
| Tutorial_OutsideFenceTrigger / Tutorial_InsideFenceTrigger | W (PlayerTrigger calls) StartTracking/Trigger, OnPlayerEnter/Leave, reminder popup | map | via PlayerTrigger | A (AS) |
| Tutorial_DoSthOnPlayerEntersBabaJagasYardForTheFirstTime | C InvokeDoSth | - | - | A (AS) |
| Tutorial_FenceGateLock | C Close/Open/OpenAfterJournal/TryOpen | poi | - | A (CS) |
| Tutorial_BabaHutLock | C `Open` | completable | - | A (PS) / C if a guest's copy opens it itself [?] |
| CollectibleJournal | I (via PlayerDetection) MakeCollectible, OnCollect | completable | - | A (SI) |
| DisableAfterJournalIntro / ModifyStateDependingOnJournalType / Journal_DoSthOnJournalClosed / Journal_DoSthOnSpecificPageOpened | C UI | - | local journal | E; Journal_DoSthOnJournalClosed: C/? if its event is story (old woman's "one more thing" was this kind; TS now blocks the piece) |
| ManageCollectTutorialPrompts | W (PlayerTrigger), OnEquippedBirds | completable | local | E (AS keeps it local) |
| HideTeleportTalismanUntilYouGetIt / OverrideTeleportToHereInTutorial | L / C GameEvents.Home.OverrideBackPort | - | local backport | E (per player teleport target) [?: each guest's backport goes to the right spot] |
| TutorialPineTwigEventReceiver | C GameEvents.Tutorial.OnGetPineBranch -> OnGotPineBranch; ScheduleBabaCheck | - | host camera | A by upstream; C on guest (EnsureBabaFindable teleports Baba) |
| Tutorial_CheckForPineStickAndGiveNewOne | F `Update` (pine stick missing from pack -> give one + StandaloneDialogue) | - | shared pack | C prefix `Update` on guest (otherwise two copies give one each) |
| RitualIdentifier | C OnGetPineBranch, OnRitualFinished | statics -> gs (hasCompletedChickenLegsRitual) | - | A by upstream; D: statics on guest must follow (TRS/SVS?) [?] |
| RitualSaveState / RitualEndSaveState / TutorialSaveState / TutorialSavedActiveStates / TutorialBuildingSaveState | L save parts | tut | - | E on guest (guests never save); host's is the truth |
| TutorialMusicController | C GameEvents.Tutorial setters (ApproachedGate ... RitualFinished) | map | - | D: guest's intensity follows only if the events fire on the guest; mirror its value or stream the events (M6) |
| TutorialSound / TutorialIntensityParamHelper / Tutorial_DestructionTutorialPopup / TutorialElement | F/C | - | local | E (popup: POP) |
| Tutorial_DisableBabaDuringTut / DeactivateHomeForTutorial | C GameEvents.Tutorial | gs | - | A (SVS) |
| Tutorial_DisableUntilRitualStarts / Tutorial_DoSthOnChickenHutEnabled / Tutorial_DeavtivateTutJaga / OnEnableEventsIfTutorial / EnableOnTutorialDemoFinished | L/C | - | - | A (ACT for on/off); OnEnable-driven events: ? |
| Tutorial_RitualSiteTrigger | C GameEvents.Player.OnStart/StopCarryingObject | - | local carry | B (M5) |
| Tutorial_AdditionalFunctionality | C GiveStartMoney/GiveItemsToCraftSomething | pack | - | C on guest (pack would double) [G: called from a talk, so host-only already] |
| TutorialChickenLegsManager / Tut_TempPortHouseHere | C GetChickenLegsAndStandUp, ParkLegsOffMap, PortHouse | - | hut TransformReference | A (HUT) + C on guest |
| GiveStartingItemsOnSkippedTutorial / DoSthOnSkippedTutorial / TutorialSkipper_DoSthOnSkip | C GameEvents.Tutorial.OnSkippedTutorial | - | - | C on guest (unlocks/BBs would double) |
| AssignTutorialTerrainInEditor / TutorialIdentifier | - | - | - | E |

### Tutorial beats in order (inferred)

Order from `GameEvents.Tutorial` field order, `TutorialEventReceiver.EventStep` numbers (0 start, 5 reveal talisman,
7 take talisman, 10 return with talisman, 15 ritual finished, 16 basic building, 18 finished),
`TutorialMusicController` setter order, `RitualStep` and the `OnScreen_*` prompt strings. Steps 1 to 4 are covered by
the `door` and `yard` regression scenarios.

| # | Beat | Types | Status |
|---|---|---|---|
| 1 | Move, look, sprint, jump | TutorialManager_v2 StartCheck_*, Tutorial_TrackedValues, LanternTutorialLogic.OnFinished_BasicMovement | A (TS) |
| 2 | Take lantern, equip (radial), raise, walk through fog | LanternTutorialLogic, PlayerDetection (lantern), UseItem_Lantern, MonitorLanternRaising, fog trigger (PlayerTrigger/ObjectDetector [G]) | A (TS, SI?, SVS unlock) [?: lantern pickup is SI or local] |
| 3 | Approach gate, door talk ("Fu, fu!"), let in | Tutorial_OutsideFenceTrigger, PlayerTrigger, TutorialNPCLogic, Tutorial_FenceGateLock | done (door) |
| 4 | Enter yard, her talk, take book, journal intro (open journal, log, honeycomb, close), gate reopens, "one more thing" | Tutorial_InsideFenceTrigger, CollectibleJournal, DisableAfterJournalIntro, TutorialDialoguePiece | done (yard) |
| 5 | Exit garden; collect close (melee birds: mushrooms) and ranged (wax) | ManageCollectTutorialPrompts, UseItem_Birds, CollectibleID, TasksTracker trial-0 tasks | A (WO, TS, TRS Stats) [?: the "first three tasks" of Tutorial_InsideFenceTrigger] |
| 6 | Talisman location revealed; Lutki: four loose planks, Lutki stands | TutorialEventReceiver(5), LifelessLutkiManager, GenericPOICondition x4, CarriableObject/CollectibleID | A (CS, WO) now; M1 for the pickup's story listeners under host-first |
| 7 | Take talisman (backport unlock), first teleport home | Poi_Tutorial_TeleportTalisman, UseItem_Backport, GameEvents.Home.TriggerBackPort/OnBackPort, OverrideTeleportToHereInTutorial, TS task "Teleport" | A (SI, SVS, TS) |
| 8 | Return with talisman; Baba talk; pine-branch task revealed | TutorialNPCLogic.RekaReturnWithTalisman, OnBabaRevealsPinewoodBranchTask, RevealCrookedPineLocation | A (DLG DefaultTalk, DS) |
| 9 | Find crooked pine (lantern reveal), take the pine branch | POI_Demo_CrookedPineBranch (revealed/picked), RevealWithLantern-style UseItemInteract [G], GivePlayerItemsHelper, RitualIdentifier.OnGetPineBranch, TutorialPineTwigEventReceiver, Tutorial_CheckForPineStickAndGiveNewOne | pick A (SI); reveal B (M8); CheckForPineStick C |
| 10 | Baba invites into her hut; enter/exit her house | OnBabaInvitedMeIntoTheHut, Tutorial_BabaHutLock, OnPlayerEnter/ExitBabaHouse (trigger type ? ) | ? probe which trigger raises the house events |
| 11 | Fetch the chicken (go back with nothing / with chicken) | CarriableObject (chicken), TasksTracker.OnGotChicken, Tutorial_RitualSiteTrigger, RitualStep.ComeBackWithChicken | CAR A; B (M5 carry events) |
| 12 | Ritual site revealed; place chicken | RitualProgressionAdvancer (OnRevealedSite, PlacedChicken), CarriableObjectPlacementHelper / MultipleObjectDetector [G] | B (RitualAdvance) |
| 13 | Cleanse the sites with the pine branch | UseItemInteractArea + UseItemInteract (PineBranch), RitualStep CleansedSingleSite/HasCleansedAllSites, TasksTracker counter | B (M8); PS fill-up A today |
| 14 | Egg: take from nest, bring, place in oven | ChickenLayEgg, CarriableObject (egg), ObjectDetector (oven, switched on by pickup), RitualStep BroughtEgg/PlacedEgg | A (DS Enter intent, CAR) + B RitualAdvance |
| 15 | Place offerings | POI_Ritual_SupplyToPlace / PlaceableObject [G], RitualStep PlacedSingleOffering/All | B (M2) |
| 16 | Receive the talisman (Svarog charm), dissolve the bones | UseItem_SvarogCharm, BoneDissolver (near the hand holding the charm), RitualStep ReceivedTalisman/DissolvedSingleBone/All | SVS A; BoneDissolver C + M4 |
| 17 | Embers: start, pick up, bring, give to Baba | RitualStep OnStartGettingEmbers/BroughtEmbers/GaveEmbersToBaba, EmberSound, CarriableObject [G], talk | B RitualAdvance + CAR + DLG |
| 18 | Chicken-legs hut rises, ritual finished | TriggerSetActiveChickenHouse, TutorialChickenLegsManager, RitualEndSaveState, RitualIdentifier, TasksTracker.OnRitualFinished | A (HUT) + D (RitualIdentifier statics) |
| 19 | Building tutorial (rebuild shack, basic building, decorating) | TutorialManager_v2 building tasks | skipped in co-op (SkipBuildingTutorial) |
| 20 | Talk to Jaga / control the hut to move on; leave the map | ChickenLegsInteract, BeyondEdgeOfMapHouseTravelTrigger, MapBorderFogManager, OnFinishedTutorial | A (HUT, WT) + C travel trigger on guest |

## 6. DoSthIf* / DoSthOn* helpers and GameEvents groups

DoSthIf* (all `Check()` called from a UnityEvent; evaluate shared state, fire `something`/`otherwise`): DoSthIfBBIsBuilt,
DoSthIfConditionIs, DoSthIfPlacedItemIs, DoSthIfPlayerCompletedQuest, DoSthIfPlayerHasBuildingBlock,
DoSthIfPlayerHasExcessItems, DoSthIfPlayerHasItem, DoSthIfPlayerHasRecipe, DoSthIfQuestStepStateIsRight,
DoSthIfStaticConditionIsTrue, DoSthIfThisIsActive -> one row, **A by upstream** (they run where their caller runs; once
callers are silenced on guests, so are they). DoSthIfResolutionLow -> E.

| Type | Trigger | Bucket / note |
|---|---|---|
| DoSthIfTransformIsClose | F `Update` every checkEveryXSeconds, TransformReference (player) | C prefix `Update` on guest + M4 on host |
| DoSthOnEnable / DoSthOnDisable / DoSthOnDestroy / POI_EventOnActivatedGO | L enable/disable | ? fire on guests when ACT switches objects; probe targets, C those with story targets |
| DoSthOnStart / DoSthOnStartIfInChapter / DoSthOnMapFullyLoaded / DoSthOnMapGenerationFinished / DoSthOnMapLeave | L (GameEvents.Map) | E [?] load-time setup, same on every copy; probe for story setters |
| DoSthOnSkippedTutorial / DoSthOnDemoStart / DoSthOnExitPlayMode | L | C on guest (skip) / E |
| DoSthOnPlayerGetItem | C (inventory add) | C on guest [?] pack is shared, so both copies see the add |
| DoSthOnEncounterStarted / DoSthOnHutToggleStanding / DoSthOnEnterBuildingMode / DoSthOnLeshyApprovalChanged | C game events | A by upstream (encounters, hut: host); LeshyApproval `Update` C on guest |
| DoSthOnHitGround / DoSthOnClosePluck | C physics/pluck | E (object local) / M1 if story |
| DoSthRandom / DoSthRepeatedly | C / T | ? (random: copies roll differently) C on guest |
| ActiveStateObserver family (QuestPageHelper, QuestPageHelperTextAdder, DialogueOptionSetter*, RumorSource, LogToJournalHelper, AddSomethingToPoiLoot, SetThisAsAITargetIfActive, DialogueMoodSetter, ActiveStateObserverHelper) | L active state | E/D: follow ACT on guests; AddSomethingToPoiLoot A (RS); RumorSource/DialogueOptionSetter matter only on host (talks run there) |
| GameEventInvoker / PlayerEventInvoker / RegularGameEventStarter / FakeButtonEventInvoker | C | E (local camera, building mode, candle) ; RegularGameEventStarter.RewardItems A (RS) [?] |

GameEvents groups the story listens to (static Il2Cpp Actions; each copy fires its own):

| Group | Story-relevant fields | Raised by | Bucket |
|---|---|---|---|
| Tutorial | all ~75 (TriggerSolveCurrentTutorialTask/Block, OnFinished_* steps, Ritual_* steps, OnSkippedTutorial, OnFinishedTutorial) | tutorial pieces, RitualProgressionAdvancer, TutorialManager_v2 | A for task solving (TS); B for Ritual_* (RitualAdvance); D (M6) for guest presentation |
| Progression | OnFinishedQuestStep, OnTrialCompleted, TriggerStartNextTrial(_MidMap), OnTrialTitleCardDismissed, OnTrialIntroDialogueFinished, OnTrialConcluded, OnTaskComplete, ForceStatsUpdate | TasksTracker, QuestTaskStepEventForwarder, PopUp_Chapter | A (TRS) + C on guest for the dismiss/intro pair |
| ExplorePhase | OnPOIDiscovered/Solved, OnAnyPoiSolved, OnEnterVillage, OnTraded, OnCollectibleCollectedOrDestroyed | PoiQuestInterface, StatsTracker | A (TRS Stats); C on guest for PoiQuestInterface |
| POICommunication | VisitPoi, RegisterInventory, GetOwnedAmountOfItem (Funcs) | PoiQuestInterface | A (shared pack) |
| SaveSystem | OnStaticGameConditionChanged, OnLoadFullyComplete | StaticGameCondition | A (SS) |
| Map / Environment | OnMapFullyLoaded, OnMapLeave, TriggerJumpTo*, OnGenerationFinished, OnSunRise/Set, time skips | MapChanger, generation | A (WT) |
| Home | TriggerBackPort/OnBackPort, OverrideBackPort, OnEnteredHomeBase/Exited, OnPlayerBoardedHut, StartControllingLegs, OnHutStandingChanged | local player, hut | E per player (backport, home base); A (HUT) for hut |
| Player | OnStartRitual/OnFinishRitual, OnPlaceItem, OnPickupItem, OnStart/StopCarryingObject, TriggerSpecificPort*, OnInteract, SitDown | the local player only | B (M5) for the carry/place/ritual ones the host's story listens to |
| Player.UseItems / Player.Abilities | OnEquip_Lantern, OnRaisedLantern, OnCollect, LightCandle, OnCollectibleHit | local tools | E mostly; M5 where a story listener needs a guest's (LanternTutorialLogic, StatsTracker) |
| AI | OnTalkRequested(GeneralAIController) | AI | A (DLG, AP) |
| Cheats | SkipCurrentChickenRitualStep, SkipNextTask, SkipCurrentTrial | dev | E (host only) |

## Counts per bucket (primary bucket per row)

Counted from the 172 table rows above (a family row counts once; the tutorial beat table and the prose lists are not
counted). Many A rows also carry a C or M-item for phase 3/4; D is low because most host-to-guest state already
travels through PS/ACT/SS/TRS.

| A | B | C | D | E | ? |
|---|---|---|---|---|---|
| 66 | 22 | 48 | 3 | 28 | 5 |

## New mechanisms needed (distinct)

1. **M1 UnityEvent splitter, generalized from AreaSync.** `Sort/Switch` per persistent listener into story and local,
   applied to every event a pickup or placement fires on the copy that did it: CarriableObject OnPickUp/OnFirstPickUp/
   OnDrop, CollectibleID OnPluck/OnCollectAction, CarriableObjectPlacementHelper onPlace, PlaceableObject onPlaced,
   DestructibleObject onDestruction/OnBreak, Readable onCompleted, BoneDissolver OnAmountDissolved. Guest: story half
   off, the act goes to the host (WO/CAR already report it); host: runs the story half under ActingPlayer.
2. **M2 Interact intent for all story PlayerDetections.** Mark as story every PlayerDetection referenced by a
   QuestTask (`Detection`, `Prompt`, `PickUpPrompt`, `PlacePrompt`), POI_Ritual, POI_Ritual_SupplyToPlace,
   PlaceableObject, POI_PlayerAreaTrigger, ItemRequester; or flip SI to "story unless local" (stations, chests,
   buyables, sitting, carry, hut, mirror). Uses the existing Interact kind. Covers 9 QuestTask/POI types at once.
3. **M3 Guest silence prefixes (one list, `WorldSync.IsGuest -> return false`).** `QuestTask.Complete`;
   `QuestRunner.Update/StartQuest/ReportDone`; `QuestStagingTask.Update` (6 types); `UseItemQuestTask.Update`
   (8 types); the remaining QuestTask `Update`s (~14); `TasksTracker.Update/Evaluate/EvaluateInFiveFrames/
   OnFinishedQuestStep/OnTrialTitleCartDismissed/StartChapterIntroDialogue` and its Ritual_* handlers;
   `QuestTaskStepEventForwarder.Advance`; `POI_Event.Trigger`, `POI_Controller.Update`, `POI_InvokeEventAfterTime.Invoke`,
   `POI_MultiRitual.Update`, `POI_MultiConditionLerp.LateUpdate`, `POI_MultiObjectEventManager.TriggerEvents`,
   `POI_EventAfter*.InvokeEvent`, `POI_EventOnNotVisible.Update`, `POI_ItemConverter.Update`, `POI_VillagerAssigner.Update`,
   `PoiQuestInterface.CheckPlayerProximity/SolvePoi/StartQuest`, `OnlyDoSomethingOnce.TryToDoSomething`,
   `Tutorial_CheckForPineStickAndGiveNewOne.Update`, skip-tutorial listeners, `BeyondEdgeOfMapHouseTravelTrigger.Update`,
   `MapBorderFogManager.TriggerTravel`, `TrialLocation.*`, encounter managers. About 45 patches, all the same shape.
4. **M4 Nearest-player swap around story Updates.** Extend ActingPlayer's `Before/After` (today only
   GeneralAIController) to the host-side Updates that test where "the player" is: ReachArea/Monologue/Talk QuestTasks,
   PoiQuestInterface, DoSthIfTransformIsClose, BoneDissolver, WeatherSensor, TrapNearbySpirit, POI_Monologue. Works only
   if they read the swapped TransformReference (probe QuestAi.get_Player first); otherwise postfix the predicates
   (`TalkQuestTask.PlayerWithin`, `MonologueQuestTask.PlayerInArea`, `ReachAreaQuestTask.Contains`) to OR over puppets.
5. **M5 Player-event intent.** A guest's GameEvents.Player OnStart/StopCarryingObject, OnPlaceItem, OnPickupItem,
   OnStartRitual/OnFinishRitual (and UseItems lantern events if needed) go to the host, which invokes only the story
   listeners of that Action (filter the invocation list by target type: TasksTracker, Tutorial_RitualSiteTrigger,
   LanternTutorialLogic, StatsTracker), under ActingPlayer. New intent kind.
6. **M6 Presentation of host story events on guests.** Either stream selected GameEvents.Tutorial/Progression
   invocations to guests with story listeners filtered out, or mirror the few presentation states
   (TutorialMusicController intensity, RitualIdentifier statics). Mirroring is smaller; pick per case.
7. **M7 AreaSync for other physics triggers.** Same AreaEnter/AreaExit kinds, an adapter per type (enter/exit method,
   event fields, "player inside" count): POI_PlayerAreaTrigger, POI_Ritual_PlayerAreaTrigger,
   CombinedTriggerElement/CombinedTrigger, BirdsStartFlyingTrigger, TriggerDayTimeChanger (SlowZone only if story).
8. **M8 UseItem intent.** A guest's `UseItemInteract.Interact/StopInteraction` (and `UseItemInteract_OverTime`
   start/stop) on a story target go to the host as (path, ItemType); the host runs Interact under ActingPlayer. Covers
   pine-branch cleansing, lantern reveal, watering, lighting/extinguishing, the 8 UseItemQuestTask types. Needs a way
   past `CheckEquippedItem`, which reads the host's own tool.
9. **M9 RitualAdvance entry.** Guest prefix `RitualProgressionAdvancer.Advance` -> Call intent (new Entry), host
   runs its own. One patch for all 16 ritual steps [G: Advance is the scene's ritual hook].
10. **M10 HandIn intent.** Item-giving UIs that run on the guest (POI_Logic_RequestItems inventory close,
    POI_Logic_SacrificialStone, FoodVillagerLogic table, PoI_Logic_Patient): the guest sends the chosen items, the host
    runs the evaluation and reward. New kind; the UIs differ, so this is the largest per-type item.

## Top risks and unknowns

1. **Inlined natives.** Small predicates (`Contains`, `PlayerWithin`, `PlayerInArea`, `CheckEquippedItem`) may be
   inlined by IL2CPP's C++ compiler; a Harmony patch on them would then miss calls from native code. Prefer patching
   the Update/entry point, and probe with a log counter.
2. **QuestAi.get_Player and other player reads.** Whether quest tasks, PoiQuestInterface, BoneDissolver and
   DoSthIfTransformIsClose read the TransformReference that ActingPlayer swaps, or a cached Transform. Decides M4.
3. **State apply replays effects.** PS applying a QuestRunner/POI_States/WorldChangeQuestTask state on a guest calls
   `OnStateApplied`/`SimulateTimelineHavingPlayed`; some of those start talks, AI orders or effects. Probe with a
   generated quest before silencing.
4. **Runtime listeners are invisible.** QuestTasks hook PlayerDetection and UnityEvents with `AddListener` (Listen),
   which `GetPersistentEventCount` does not show; M1/M2 must find quest-owned detections by scanning the tasks.
5. **Host's own player in host-run guest actions.** POI_PlayerFunctions/POI_PlayerEventSender (block movement, hold
   item, sit down) and UseItemInteract's equipped-item check act on the host's body when the host runs a guest's story.
   Needs routing those calls to the acting guest as a one-off (D).
6. **Shared static Actions.** GameEvents are static delegates: invoking one on the host for a guest (M5) also runs the
   host's own camera/animation listeners unless the invocation list is filtered; Il2Cpp delegate lists need a probe.
7. **OnEnable-driven story on guests.** ActiveSync switching objects on fires DoSthOnEnable, POI_EventOnActivatedGO,
   StaticGameCondition onConditionTrueOnEnable on guests; list their targets with the `listeners` dev command.
8. **Random and per-copy rolls.** DoSthRandom, POI_VillagerAssigner, ChickenLayEgg timers, LeshyEncounter spawns roll
   per copy; host-first needs them host-only with results mirrored.
9. **Far-from-host logic.** Quest Updates and detectors may be culled or throttled away from the host's player
   (PLAN risk 2); a guest-only quest site may not run on the host at all. Probe a QuestRunner with the host far away.
10. **Ritual hook guess.** M9 assumes the ritual's steps all pass through RitualProgressionAdvancer.Advance; if some
    raise GameEvents.Tutorial.Ritual_* directly from UnityEvents, those need M1 instead. Probe with `listeners Advance`.
