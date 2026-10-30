# Slice: "Same world, same day" (world gen, time of day, weather, chapter)

Static research only (interop signatures via dumper + save XML, save "host/- 1 -/1" and its $AUTOSAVE$1, tutorial map, 72 s playtime).
Tags: **[V]** = seen in a signature, enum or save data. **[I]** = inferred, needs a runtime check.
Bodies are IL2CPP stubs, so all claims about *what a method does* are [I] unless noted.

## 1. Who owns the state

### World generation
- `EnvironmentGenManager : MonoBehaviour` [V]
  - `EnvironmentRandomState lastUsedState`, `stateForNextGeneration`, `bool generateNextEnvFromState` [V]
  - `bool useSeedOverride`, `int seedOverride`, `TasksTracker.ChapterTag chapterOverride` [V]
  - `SaveableMap loadedMap`, `Texture2D heightMap`, `Dictionary<ObjectTypeCollection, List<SerTransform_OTC>> otcState` [V]
  - `ChapterGenerationSettings currentChapterGenerationSettings`, `ChapterPoiPlan chapterPoiPlan`, `MapToSpawnInfos nextMapInfos`, `CurrentChapterInfo currentChapterInfo` [V]
  - Methods: `void GenerateEnvironment(bool generateSameMap)`, `IEnumerator GenerateEnvironmentCoroutin(bool generateSameMap, bool isPoiRegenerationRetry)`, `void SaveOrLoadState(GenStep step)`, `bool IsRandomStateInvalid()`, `void ReseedAfterYield()`, `void RollCurrentBiomes()`, `Task PreparePlannedPoiList(bool)`, `bool RequestPoiRegeneration(string requester)`, `ChapterPrediction PredictChapterForThisMap()`, `void OnMapLeave()` [V]
  - Events: `Action OnGenerationStarted`, `OnGenerationFinished`, `OnGenerationFinishedFromScratch`, `Action<string> OnGenerationFailed`, `Action TriggerEnvGenStateSnapShot` [V]
- `EnvironmentGenManager.GenStep` enum: SetShaderVariables 0, GenerateAllNoises 1, GenerateBiomes 2, GenerateRiverNetwork 3, GeneratePathNetwork 4, SpawnPOIs 5, GenerateTerrain 6, PlacePathNetworkOnTerrain 7, PlacePOIsOnTerrain 8, GenerateObjects 9 [V]
- `EnvironmentGenManager.EnvironmentRandomState` [V]: one `int pre_<Step>` per GenStep (10 ints), `List<SerializableOTCState> serializableOTCStates`, `string debug`;
  methods `InitializeWithDerivedSeed(GenStep step, int masterSeed)`, `SeedStep(GenStep, int newSeed)`, `InitializedAndSnapshotNewState(GenStep)`, `ReloadState(GenStep)`, `GetOTCStates/SetOTCStates(..., OTCTypeReference)`.
  `SerializableOTCState { string ID; List<SerTransform_OTC> serializableTransforms }`, `SerTransform_OTC { SerVector3 pos; float yRot; int subBiomeIndex; int objectIndex }` [V]
- `MapLoader : MonoBehaviour` — `LoadTutorialMap()`, `GenerateMap()`, `ClearMap()`, `bool lastGeneratedWasTutorial`, tutorial vs regular `TerrainData` [V]
- `MapChanger : MonoBehaviour` — `JumpToNewMap()`, `JumpToSameMap()`, `JumpToTutorial()`, `JumpToMap(LoadableHandPlacedMap)`, `LeaveCurrentMap()`, `FinishGenJump()`; fields `tutorial`, `CC` (hand-placed maps) [V]
- `SaveLoadMap : MonoBehaviour` — `RestoreMapState(SaveableMap map, bool isTutorial)`, `SaveMapElement(SaveableMap, ISaveableMapElement)`, `RestoreStateForGeneratedMapElements()`, `Texture2D GetHeightMap()`, `static bool isLoading`, `bool isOnLoadedMap` [V]
- `ISaveableMapElement { GetStateForSaving(SaveableMap); RestoreSavedState(SaveableMap) }` — the per-component save contract (WeatherManager implements the same two methods) [V]
- `ChapterGenerationSettings : ScriptableObject { int trial; List<BiomeSpawnChance> biomeSpawnChances; POI_SpawnPreset poiSpawnPreset; GetWeightedBiomeSpawnChance() }` — static per-chapter tuning asset, not state [V]
- `MapAuditSeed { string step; int seed }` and `BenchmarkDriver._TravelToSeededMap_d__61` — debug/benchmark tooling exists that travels to a seeded map [V]
- `GroundHeightmapGlobals` (static, shader binding only) [V]. `FileIO.LEGACY_HEIGHT_MAP_FILE` + `FileIO.DeleteLegacyHeightMaps(...)` [V] → HeightMap.xml is a legacy part that the game now deletes; no HeightMap.xml in the saves [V]. Heightmap is regenerated at load [I].

### Time of day
- `DaytimeManager : MonoBehaviour` [V]: `float time`, `lengthOfDay`, `sunRiseTime`, `sunSetTime`, `bool isDay`, `DayTimeTracker tracker`, `WeatherManager weatherManager`;
  methods `Update()`, `PassTime()`, `SkipTimeTo(float targetHour)`, `TriggerDaySkip()`, `TriggerNightSkip()`, `TriggerSetToMorning()`, `SetToRandomEarlyDaytime()`.
- `DayTimeTracker : ScriptableObject { float dayTime; bool isDay }` — shared read-model for other systems [V]
- `WeatherManager` also has `float dayTime`, `int dayIndex`, `float lengthOfDay`, `SetDayTime(float hour)`, `AdvanceDay()`, `ChangeDaytime(float target, float time, bool stayInOverride)`, `SimulateSkippedTime(float realMinutesSkipped)`, `static float MAX_SKIP_SIMULATION_HOURS` [V].
  The save stores time only as `SerializableWeather.dayTime` + `dayIndex` [V] → WeatherManager is the persisted owner of the clock [I]; which of the two `Update`s advances it is unknown [I].
- Sleep: `Interactable_Logic_Bed.SleepTillDawn()`, `SleepTillDusk()`, `static TriggerFade()`, `UnityEvent onSleep`; `GameEvents.UI.Interactables.Bed.OnSunIconClicked/OnMoonIconClicked` [V]. `TriggerDayTimeChanger` (trigger volume, `daytimeToSet`, `oldDaytime`) forces time while inside [V].

### Weather
- `WeatherManager : MonoBehaviour` [V] live fields: `dayTime, windIntensity, rainIntensity, groundWetness, cloudCoverage, precipitation, cloudType, fogDensity, fogPotential, groundMist, atmosphere, puddleAmount, dayIndex, bool manualOverride, randomize, keepManualOverride, dontUpdate`, plus ~40 forecast timers/goals.
  - Snapshot API (ideal for sync) [V]: `SerializableWeather CaptureWeatherSnapshot()`, `void ApplyWeatherSnapshot(SerializableWeather)`, `SerializableWeatherForecast CaptureForecast()`, `void RestoreForecast(SerializableWeatherForecast)`, `GetStateForSaving(SaveableMap)`, `RestoreSavedState(SaveableMap)`, `StartSimAfterLoad()`.
  - Randomness [V names]: `RandomizeWeather(bool onlyDay)`, `RollMoonPhase()`, `RollFogPotential()`, `RollAtmosphereGoal()`, `RollCloudCoverageGoal()`, `RollPrecipitationGoal()`, `RollCloudTypeGoal()`, `RollWindGoal()`, `RollSpellIntensity()`, `BeginHeadlessSimulation(int seed)`, `ApplyRandomRainyWeather/DryWeather()`, `UpdateWeatherOnMapChange()`.
  - Scripted changes: `ApplyWeatherPreset(WeatherPreset)`, `ApplyWeatherState(cloudCov, cloudTyp, precip, rain, wind, front, fog)`, `Change{CloudPrecipitation,CloudCoverage,ChanceOfRain,Fog,GroundMist,Wind,Daytime}(target, time, stayInOverride)`, `PlayWeatherEvent(WeatherEvent)`, `TriggerLightning()`, `ForceCloudOffset(...)`; driven by `WeatherManipulator` POIs (`Tut_WeatherManipulators` in the tutorial save) [V].
- `SerializableWeather` (22 fields, ~70 incl. forecast) [V]; sample: dayTime 8.83, dayIndex 11, manualOverride true, identical in the autosave and the slot save 1 min apart [V data] → tutorial clock appears frozen by manualOverride [I].
- `WeatherTracker : ScriptableObject { rainIntensity, windIntensity, fogIntensity }` [V].

### Chapter / progression
- `TasksTracker` statics [V]: `ChapterState currentChapterState`, `ChapterTag CurrentChapterTag`, `bool completedTrialOnCurrentMap`, `List<QuestStepID> completedSteps`; instance `int _currentTrialIndex`, `List<Trial> trials`, `SyncCurrentChapterMirrors()`, `static ChapterTag ChapterTagForTrialIndex(int)`.
- `ChapterState` enum: None, InProgress_withTrialLocation, InProgress_withoutTrialLocation, Completed, CompletedEverything [V]. `ChapterTag`: TrialOfTheChicken 10 ... TheWait 120, Sandbox 9999 [V].
- `CurrentChapterInfo : ScriptableObject { int currentTrialNumber; Trial currentTrial; bool currentTrialIsSuspended; List<EnvironmentGenerationPreset> mainQuestMapsToSpawnNext }` [V]
- Persisted in Progress.xml (`currentTrialIndex`, `completedChapterIDs`, `completedTaskIDs`, `completedQuestSteps`, `staticGameInformation`, use-item unlocks) and Meta.xml [V data].

### Pause
- `CanvasManager.OpenPauseMenu()`, `SetPauseMenu(bool)`, `ForbidPauseScreen(PauseForbidReason)`, `bool isUiOpen_Pause`; `GameEvents.UI.PauseMenu.OnUiOpened_Pause/OnUiClosed_Pause` [V]. `PauseFmodEventWhileGamePaused`, `PauseVisualEffectsWhileGamePaused` exist [V] → pause likely sets `Time.timeScale = 0` [I].
- `CurrentGameMode.GameMode`: Regular, Building, InGameUI, Menu, UsingRadialMenu, LoadingScreen [V].

### Save layout and size (this early save)
| Part | Raw | gzip -9 | Notes |
|---|---|---|---|
| CurrentMap_c.xml (`SaveableMap`) | 69.7 KB (already gzip) → 768 KB XML | 69.7 KB | POIs, stations, AI, weather, OTC state [V] |
| FogMap.xml | 88 KB | 10.9 KB | base64 PNG, 2048x2048 RGB, fog-of-war (exploration), not weather [V] |
| House.xml | 78 KB | 4.2 KB | building blocks [V] |
| Thumbnail.xml | 26 KB | — | JPEG, not needed |
| Progress/Inventory/Character/Collection/Stats/Meta/Guidance/PoiHistory | ~19 KB | ~2 KB | [V] |
| **All except thumbnail** | 276 KB | **88 KB tar.gz** | [V] |
`SaveableMap.mapGenState` exists [V] but is absent from this tutorial save [V data]; on generated maps it carries all OTC scatter transforms and will dominate map size [I]. `FileIO.s_compressingMapFiles`, `CompressPlainMapFilesInBackground()` [V] → map part is gzip on disk.

## 2. Deterministic vs must-sync

| State | Verdict |
|---|---|
| Terrain/biomes/rivers/paths of a generated map | Deterministic *given* `EnvironmentRandomState` (per-step seeds) [I, strongly suggested by `InitializeWithDerivedSeed`, `SaveOrLoadState`, `ReloadState`, `generateNextEnvFromState`, `regenerateTerrain`]. Must sync the state, not a single seed. |
| Object scatter (trees, rocks) | Saved explicitly as OTC transforms [V] → designers did not trust reseeding for it (raycasts/GPU/frame timing) [I]. Ship OTC states. |
| POI selection & placement, POI element states, AI | Saved as data in `SaveableMap.pois/poiStates/...` [V] → not derivable; must sync. |
| Tutorial / CC maps | Hand-placed scenes (`MapChanger.tutorial/CC`) [V] → deterministic except saved element states. |
| New map on travel (`JumpToNewMap`) | **Not** deterministic across machines: new master seed per machine [I]. Biggest drift source. |
| Time of day | Deterministic rate (lengthOfDay) but drifts from frame timing, pause, sleep, `TriggerDayTimeChanger`, load timing [I]. Must sync, cheap. |
| Weather | Stochastic `Roll*` (UnityEngine.Random, also consumed by unrelated gameplay) [I] → must sync state. |
| Moon phase | from dayIndex + `RollMoonPhase` [I] → covered by dayIndex + snapshot. |
| Chapter/trial/progression | Host-authoritative events; must sync (later slice). |
| FogMap (explored area) | Per-player or union; cosmetic. Defer. |
| Heightmap | Regenerated from map state [I]; never ship. |

What makes the worlds drift: (a) any map generation run independently (new map, `JumpToSameMap`, POI regeneration retry `RequestPoiRegeneration`, `HandleOldSaves`/`RestartLeshyTrialWithRegeneratedMap` version migrations); (b) weather Roll* each machine; (c) clock dt/pauses/sleep on one side; (d) per-player scripted triggers (`TriggerDayTimeChanger`, WeatherManipulator POIs, cutscene `ChangeDaytime`); (e) client autosaves overwriting its copy; (f) client loading a stale save copy.

## 3. Proposed sync design (fits CoopSession)

Authority: host owns world, clock, weather, chapter. Client never generates a new map on its own and never autosaves into the shared slot.

### Join: whole save over the network (not seed + deltas)
Seed+deltas is not viable: the map file *is* the delta set (POI/AI/station state), and OTC scatter is stored, not reseeded. A full save is ~90 KB gzip now, likely 0.3–1 MB late game [I] — trivial on LiteNetLib ReliableOrdered (fragmented).
1. On `PlayerJoined`, host calls `SaveLoadGame.SaveCurrentSaveGame(slot, SaveGameType)` to a dedicated coop slot (or reuses the files of the last save), waits `GameEvents.SaveSystem.OnGameSavedSuccessfully`, tars the folder minus Thumbnail.xml, gzips.
2. `WorldBlob = 18`: `int transferId, int totalLen, int chunkIndex, int chunkCount, byte[] chunk` (32 KB chunks), ReliableOrdered, separate channel so 15 Hz PlayerState is not head-of-line blocked.
3. Client writes into `FileIO.SaveFilesRootPath/<coop>/1/`, then `MainMenu.LoadSaveGame(folder, slot)` (already used by `-coopautoload`).
4. Client replies `WorldReady = 19` (empty) after `GameEvents.SaveSystem.OnLoadFullyComplete`; host then starts clock/weather streams to it.
Client disables saving: Harmony prefix `SaveLoadGame.SaveCurrentSaveGame` / `DoSpecialSave` → return false on client (or redirect to a throwaway slot).

### Clock + weather stream
`WorldClock = 20` (host → all), Unreliable/Sequenced, 1 Hz plus immediately on any jump:
`float dayTime, int dayIndex, byte flags (manualOverride, isDay, paused), float timeScaleHint`.
Client: postfix on `WeatherManager.Update` (or `DaytimeManager.PassTime`) lerps/snaps local `dayTime` toward host if |Δ| > 0.05 h; snap when > 0.5 h. Use `WeatherManager.SetDayTime(hour)` for snaps.

`WeatherState = 21` (host → all), ReliableOrdered, every 10 s and on change events:
the `SerializableWeather` fields (≈22 floats/ints/bools, ~100 bytes; forecast optional, it only matters if the client simulates). Client applies via `ApplyWeatherSnapshot(snapshot)` with `keepManualOverride`/`dontUpdate` so it doesn't run its own forecast.
Client suppression (Harmony prefixes returning false when role == Client): `WeatherManager.RandomizeWeather`, `Roll*Goal`, `RollMoonPhase`, `RollFogPotential`, `UpdateWeatherOnMapChange`, `UpdateClouds/UpdateFront` (forecast evolution) — keep visual-only `ApplyWeatherLighting`, `SetWeatherVariables`.
Host side capture: postfix on `ApplyWeatherPreset`, `ApplyWeatherState`, `Change*`, `PlayWeatherEvent`, `SetDayTime`, `AdvanceDay`, `SimulateSkippedTime`, `DaytimeManager.SkipTimeTo` → mark dirty, send next tick.

### Sleep / skips
- Host sleeps: postfix on `Interactable_Logic_Bed.SleepTillDawn/SleepTillDusk` and `DaytimeManager.SkipTimeTo` → `TimeSkip = 22` (`float targetHour, int dayIndex`, ReliableOrdered); client runs `Interactable_Logic_Bed.TriggerFade()` + `SkipTimeTo(target)` then waits for the next WeatherState.
- Client sleeps: prefix on client's Bed methods → send request to host, return false; host decides (v1: anyone may skip; later: all players in bed).
- Pause: host pause must not freeze the world for clients. v1: let each side pause locally; host clock keeps authority and client resnaps on unpause. Investigate whether host pause sets timeScale 0 (then host-side clock stops for everyone — acceptable v1).

### Map transitions (later step, same slice)
Prefix `MapChanger.JumpToNewMap` on client → block unless host-initiated. On host: postfix `EnvironmentGenManager.OnGenerationFinishedFromScratch` → serialize `lastUsedState` (EnvironmentRandomState, XmlSerializer) and send `MapGenState = 23`; client sets `stateForNextGeneration = state; generateNextEnvFromState = true` then calls `JumpToNewMap()`. Fallback if visuals diverge: host saves, ships whole save (same path as join), client reloads.

### Chapter
`ChapterSync = 24` (host → all, ReliableOrdered on change): `int currentTrialIndex, byte ChapterState, int ChapterTag, bool completedTrialOnCurrentMap`. Client writes TasksTracker statics then `SyncCurrentChapterMirrors()`. Full progression replication belongs to the quest slice.

Message summary: 18 WorldBlob (RO, chunked), 19 WorldReady (RO), 20 WorldClock (Seq, 1 Hz), 21 WeatherState (RO, 0.1 Hz + on change), 22 TimeSkip (RO), 23 MapGenState (RO, chunked), 24 ChapterSync (RO). Bump `Protocol.Version`. Host stores latest weather/clock/chapter and replays to late joiners like Appearance.

## 4. Risks / open questions (need a runtime test)
1. Does `GenerateEnvironment` with the same `stateForNextGeneration` produce identical terrain/POIs on two machines? Compare `lastUsedState.debug`, POI list, terrain height at 10 sample points. (Unity Random across yields, GPU compute `EnvironemtGen_ComputeController`, async grass loads are risks.)
2. On a *generated* map (not tutorial), is `mapGenState` serialized and how big is CurrentMap_c.xml? Need a save after leaving the tutorial.
3. Which object advances the clock: `WeatherManager.dayTime` vs `DaytimeManager.time`? Log both + `DayTimeTracker.dayTime` per second.
4. Does `ApplyWeatherSnapshot` snap visuals immediately or blend; does it re-arm the forecast (`StartSimAfterLoad`)?
5. Does pause set `Time.timeScale = 0`? Does `debugSimulationSpeed` scale the clock?
6. Tutorial `manualOverride = true` freezes time [I] — is that true, and when does it clear?
7. Does loading a save on the client while connected break PlayerSync/Appearance (PlayerMovement_2021 recreated)? Current code clears puppets when `_local == null`, so likely fine.
8. `HandleOldSaves`/`MapIsOlderThanRegenGate` may regenerate a map on load — both sides must run the same game version (check `saveGameVersionID` in Hello).
9. Client autosave triggers (`GameEvents.SaveSystem.TryAutoSave/PreAutoSave`) — confirm a prefix on `SaveCurrentSaveGame` covers all paths (`FileIO.FireAndForgetSaveSaveGame` is the lower choke point).
10. Is FogMap per-player desirable? (Design question.)
11. Il2Cpp interop: `SerializableWeather` is an Il2Cpp object; build it with `new SerializableWeather()` and set props — verify setters exist (fields shown as P) [I].

## 5. First implementation step
Clock + weather mirror, no save transfer yet (both sides already load the same save via `-coopautoload`):
- `Sync/WorldSync.cs`: host every 1 s reads `WeatherManager.dayTime/dayIndex` (find via `Object.FindFirstObjectByType<WeatherManager>()`), sends `WorldClock = 18` Sequenced; every 10 s sends `CaptureWeatherSnapshot()` fields as `WeatherState = 19` ReliableOrdered.
- Client: snap with `SetDayTime` when |Δ| > 0.25 h; `ApplyWeatherSnapshot` on WeatherState; Harmony prefix on `WeatherManager.RandomizeWeather` and the `Roll*Goal` methods returning false on client.
- Diagnostics: log host/client `dayTime`, `DaytimeManager.time`, `DayTimeTracker.dayTime`, cloudCoverage, rainIntensity every 5 s; use `mod/shot.ps1` to compare skies. Run a debug cheat (DebugWeatherSection / `ApplyWeatherPreset`) on the host and confirm the client follows.
This answers open questions 3–6 and is independent of the harder map-transition work. (If 18/19 get used here, renumber the save-transfer messages.)
