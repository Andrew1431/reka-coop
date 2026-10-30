# Build together: research (2026-10-26, buildid 26200396)

Sources: interop Assembly-CSharp.dll via dumper (signatures only), House.xml in game/{host,client}/CoopSaves/*/- 1 -/,
addressables catalog strings. Tags: **[V]** verified in a signature or data, **[I]** inferred (names/shape only, bodies are stubs).

## 1. Game classes that own building state

**Correction to NOTES "hook candidates":** `HouseBuildRegistry` is not the placement registry. It only holds
`_depositsByCorner` with `DepositCornerPartnerTris / ReadOtherPartnerTris / HasCornerPartnerTris / Clear` (mesh corner triangles) [V].
It doesn't matter for sync.

### BB_Building (MonoBehaviour, ~1300 members): build mode, placement, removal, swap, blueprints
- Statics: `static Transform Home` (hut reference frame), `static Dictionary<GameObject,BuildingBlock> placedBBs`,
  `static int _placedHouseBBCount`, `static Action<BB_Building,PlacementRefusal,BuildingBlock> PlacementRefused` [V]
- Fields: `BB_Building_UndoRedo undoRedo`, `BB_Building_CutManagerTool cutManagerTool`, `PlayerBBCollection playerBBs`,
  `InventoryCommunication comm_Inv`, `RuntimeBuildWallet _wallet`, `bool buildModeEnabled`, `bool preventPlacing`,
  `GameObject currentBBRep` (ghost), `List<BlueprintSite> _blueprintSites` [V]
- Mode: `bool TryStartBuildMode(bool gardening, bool houseBuilding)`, `bool TryStopBuildMode()`, `void SetMode(bool,bool,bool)`,
  `void ExitMode()`, `void ForbidBuildingMode(bool)` [V]
- Placement (house blocks):
  - `bool TryCommitSpec(ref HouseBlockSpec spec, bool withVfx)` [V]. Single-press commit for house blocks [I].
  - `BlockListResult CommitBlockList(HouseBlockList list, BlockListCommitMode mode)`, where mode is `PlaceWhatFits=0 | AllOrNothing=1`.
    Result fields: `int placed, int skipped, PlacementRefusal firstSkipReason` [V]. Used by drag and blueprint fill [I].
  - `GameObject SpawnHouseBlock(ref HouseBlockSpec spec)` [V]. Low-level spawn, probably no payment [I].
  - `GameObject SpawnHouseBlockInHome(ref HouseBlockSpec spec, Pose partInHome, int recordedIndex)` [V]. Spawns with a given
    placementIndex. Used by load and undo recreation [I].
  - `void HandlePlacedHouseBB(GameObject houseBB, Nullable<HouseCutSettings> cutSettings)`, `void FinishPlacedBB(GameObject)`,
    `void RegisterBB(GameObject, BuildingBlock)`, `void CountPlaced(BuildingBlock, int delta)` [V]
  - `void CommitDrag()`, `void PlacePressedBlock()`, `void PlaceBB()` [V] (input-level entry points)
- Placement (furniture / non-house BBs): `void CommitPlacement(BuildingBlock bb, Vector3 pos, Quaternion rot)`,
  `GameObject SpawnBB(BuildingBlock bb, Vector3 position, Quaternion rotation, Transform parent, HouseCutSettings cutSettings)`,
  `Transform GetNewParent(Vector3, Placement, Transform)` [V]
- Validation: `bool CheckIfHouseBBIsPlaceable()`, `bool IsHouseBlockPlaceableAt(Transform currentBB, bool areaOnly)`,
  `bool WallSlotIsTaken(HouseBuildingObject)`, `void ValidatePosition(bool)`, `bool TryFindCollidingPlacedPiece(out GameObject)`,
  `PlacementRefusal BuildBlueprintBlock(BlueprintSite, int)` [V]. The `PlacementRefusal` enum has 60 values (`None=0`,
  `OutsideHutArea=12`, `WallSlotTaken=15`, `PieceInTheWay=18`, `MissingResources=28`, `CantUndo=57`, ...) [V]
- Snapping: `TrySnapIntoAimedCell(BuildingBlock, out HouseBuildingObject)`, `TryPoseFloorOnHouseGrid(RaycastHit)`, plus helper classes
  `HouseBBGridSnap`, `HouseBBSnapContext`, `HouseBBSnapAnchors`, `BB_Building_Snapping` [V]. All of this is local aim/ghost logic and is never synced.
- Removal: `Refund StoreBB(GameObject bbG, List<…> returnTo)`, `bool CanPlayerStoreBB(GameObject, bool)`,
  `void RemoveBBFromWorld(GameObject go[, Refund refund])`, `void RemoveHouseBlockUnrecorded(GameObject go)`,
  `BuildingBlock UnregisterRemovedBB(GameObject go)`, `void TearDownRemovedBB(GameObject, BuildingBlock)`,
  `int RecursivelyStoreChildrenBBs(GameObject, int)`, `void StoreFittedBlocks(GameObject)` [V]
- Modify: `void CommitSwap()`, `GameObject SwapHouseBlockPaid(GameObject go, BuildingBlock toCard)` (material/wood swap) [V].
  Cut settings are changed through `BB_Building_CutManagerTool.ApplyCutSettings(HouseBuildingObject, HouseCutSettings)`,
  `SetCutState(HouseBuildingObject, HouseCutState)` and `TryCycleFit(HouseBuildingObject)` [V]
- Costs: `bool CanPlayerAffordThisBB(BuildingBlock)`, `bool LetPlayerPayForBB(BuildingBlock bb, List<…> materialsTakenFrom, bool noUpdate)`,
  `String MissingMaterialsText(BuildingBlock)`, `bool CanAffordSwaps(List, bool)` [V]
- Blueprints: `PressPlaceBlueprint(HouseBlueprintCard, BlueprintGhost, ref Pose)`, `RestoreBlueprintSites(...)`,
  `GetBlueprintSitesForSaving(PossessionTypeReference)`, `PlayerBlueprintLibrary _blueprintLibrary`, `PlaceCopyNow()` [V]

### Data types
- `HouseBuildingObject` (MonoBehaviour on every house piece): `HouseBBType type`, `int placementIndex`, `bool isDiagonal`,
  `Vector3 diagonalForward`, `Vector3 boardDirection`, `bool keepWhole`, `bool reversed`, `int[] refusedCutters`, `byte braceOverrides`,
  `ushort removedExtras`, `int fitTarget`, `HouseCutSettings CutSettings`, `void SetCutSettings(HouseCutSettings)` [V]
- **`HouseBlockSpec` (struct): the compact placement descriptor already used by the game** [V]:
  `BuildingBlock block; Vector3 refPosition; Quaternion refRotation; Vector3 blockLocalPosition; Quaternion blockLocalRotation;
  bool isDiagonal; Vector3 diagonalForwardRef; Vector3 boardDirectionRef; bool assignBoardDirection; HouseCutSettings cutSettings;
  bool takeStoredCutSettings; int fitTarget; int placementIndex; PlacementRefusal refusal`,
  `static HouseBlockSpec FromPlaced(HouseBuildingObject hbo, BuildingBlock card, Transform reference)` [V].
  `ref*` means the hut reference frame: `HouseContext { Transform reference; Transform partsRoot; HouseSharedSettings settings }`,
  `HouseContext.TryFrom(HouseBuildingObject, out HouseContext)` [V]
- `HouseCutSettings` (struct): `keepWhole, reversed, int[] refusedCutters, byte braceOverrides, ushort removedExtras`,
  `static From(bool, IReadOnlyList<int>, bool, byte, ushort)`, `bool Refuses(int cutterPlacementIndex)` [V].
  **`refusedCutters` holds other pieces' placementIndex values** [V by name], so placementIndex must match on every peer.
- `HouseBBPlacement` (static helpers): `static int s_lastPlacementIndex`, `AssignPlacementIndex(hbo)`,
  `RestorePlacementIndex(hbo, int)`, `ReservePlacementIndex(int)`, `RecreatedPlacementIndex(int, Dictionary)`,
  `SortAndMarkNeighboursDirty(hbo)` [V]. `HouseCutManager.PlacedBefore(a, b)` [V]: placement order drives cut/roof precedence [I].
- `HousePart` (MonoBehaviour): a grid container for a run of blocks (`gridSize`, `type`), with `MarkDirty()`,
  `static Action<HousePart> OnPartRebuilt` and `static Action OnDrainCompleted` [V]. Meshes are generated from children [I].
- `HouseBlockList { List<HouseBlockSpec> specs; static int MAX_BLOCKS; Add(ref spec) }` [V]
- `BuildingBlock : LootablePossession`: `List<ItemStack> materialCost`, `bool refundCostsInsteadOfGettingInstanceWhenStored`,
  `bool shouldRefund`, `Category category`, `HouseBBType houseBBType`, `bool IsHouseBB`, `GameObject prefab`, `Vector3Int gridSize`,
  `List<ChapterTag> cantRemoveInTheseChapters` [V]
- Variants and "painting": there is no paint system. Wood or clay variants are separate cards with their own typeID
  (`HouseBB_WallBirch`, `HouseBB_FloorPine`, `HouseBB_Roof_Steep_Beech`, `HouseBB_Clay_Beam_Vertical`, ... in catalog.bin) [V].
  Categories `HouseSpruce=110 … HouseClay=160` [V]. A recolour is a `Replace` (swap card). Decoration = furniture-category BBs
  (`WallDecor=60`, `Decoration=70`, ...), placed with `SpawnBB` [I].
- `BB_Building_UndoRedo`: `RegisterStep_Place(Vector3, Quaternion, BuildingBlock, GameObject, Transform parent, List<Portion> payment)`,
  `RegisterStep_Remove(..., Refund)`, `RegisterStep_Move(...)`, `RegisterStep_Settings(bb, go, HouseCutSettings old, HouseCutSettings new)`,
  `RegisterStep_FitTarget(bb, go, int old, int new)`, `RegisterStep_Replace(BuildingBlock old, BuildingBlock new, GameObject go)`,
  `RegisterStep_Blueprint(BlueprintSite)`, `bool ExecuteStep(Step, bool isUndo)`, `GameObject RecreateHouseBlock(Step)`,
  `bool RemoveBB(Step)`, `int doNotRegisterEventsThisFrame`. Step types: `Place, Remove, Move, Settings, Replace, Blueprint` [V].
  `Step` carries `posHouseLocal/rotHouseLocal, blockLocalPos/Rot, placementIndex, cutSettings, fitTarget, payment, refund` [V].
  **These RegisterStep_* calls are exactly the user-level commit points** [I: undo is registered on commit].
- Costs and wallet: `IBuildWallet { int Money; InstancesOf(bb); HasPlan(bb); RefundsAsInstance(bb); MoneyCost(bb); Available(Item) }`,
  implemented by `BB_Building/RuntimeBuildWallet` [V]. `HutBuildMaterials` (static): `TryTake(items, preferred, takenFrom)`,
  `Give(...)`, `HasAll(cost)`, `GetAvailable(Item)`, `HutChests`. Stores are `Backpack | Chest | Overflow` [V].
  **Building draws from the backpack and from hut chests** [V by names]. `PlayerBBCollection` (ScriptableObject): `AquiredBBs` (BBStack:
  `bb, hasPlan, amountOfInstances, instancesDisposed`), `AddAsInstance`, `RemoveInstance`, `HasPlan`, `storedCutSettings` [V].
- Events: `GameEvents.Home.BuildingMode.OnPlacedBB / OnBuiltBB / OnStoreBB(BuildingBlock)`, `OnCutStateStepped`,
  `OnHouseBBCountChanged(int)`, `OnEnter/ExitBuildMode`, `RemoveBuildingBlockFromWorld(GameObject)` [V]. The block-only args make
  them too thin for sync, but they work as triggers.
- `IHouseChangeObserver { OnBlockSorted(ref HouseContext, hbo); OnBlockRemoving(ref HouseContext, hbo); OnHouseRebuilt(ref HouseContext) }` [V]

### Save/load (House.xml = `SaveLoadBBs.SavedBBs`, field `SaveGame.savedBBs`)
- `SaveLoadBBs` (MonoBehaviour): `static bool isloading`, `BB_Building bb_Building`, `PossessionTypeReference BBIDConverter`,
  `void GetDataForSaving(ref SavedBBs)`, `void LoadData(SavedBBs)`, **`void LoadSpecificHouse(SavedBBs)`**, **`void DeleteCurrentState()`**,
  `static List ResolvePlacedBBs(...)`, `void ReturnToPlayer(List)` [V]
- `SaveLoadCustomHouse`: `SaveCustomHouse()`, `LoadCustomHouse()`, **`LoadHouseFromPath(string path)`**, `static T LoadXml<T>(string)`;
  together with `LoadCustomHousePanel` (a dev file picker) [V]. **The game already supports replacing the hut at runtime from a House XML** [I: names].
- House.xml shape [V, host save: 71 placed, 17 parts, 25 owned stacks, ~78 KB]:
  - `unlockedAndOwnedBBs/BBStackWithID{bbID, hasPlan, amountOfInstances, instancesDisposed}`: the player's own plans and stock
  - `placedBBs/BBData{gameObjectID, typeID, pos, rot, parentID, level, isEquippingID, isDiagonal, diagonalForward, placementIndex,
    boardDirection, keepWhole, refusedCutters?, reversed, braceOverrides, removedExtras, fitTarget}`. For house blocks, pos/rot are
    **local to the parent HousePart** (values like (1,0,0) and identity) [V data]. placementIndex runs 1..N [V data].
  - `houseParts/HousePartData{gameObjectID, pos, rot}`: part pose, presumably hut-local (y 1..10 m) [I]. Every BBData.parentID
    points at a HousePartData.gameObjectID [V: all 17 parent ids resolve].
  - `gameObjectID` values are negative instance-id-like ints and are remapped on load (`loadedObjectsIDLookUp: IDPairing{oldID,newID}`)
    [V types, I meaning]. **Not stable, so they can't be used as network ids.**
  - Also: `storedCutSettings`, `blueprintSites/BlueprintSiteData{cardID, framePosition/Rotation, blocks, states, layoutPlacementIndex}`,
    station state (`cauldrons`, `chests` with inventories, `plants`, `ivies`, `bbStates`, ...), `babaJagaPosition`.
- Player blueprint library: `FileIO.BlueprintLibraryPath`, `PlayerBlueprintData{version, number, savedUtc, customName, anchor,
  List<BlueprintBlockData> blocks}`. BlueprintBlockData is the same as the spec fields above [V]. Each library is per player, so it needs no sync.
  No library exists on disk yet in either copy [V].

## 2. Deterministic vs must-sync
- **Must sync:** the set of placed house blocks, each with typeID + spec poses + flags + cut settings + fitTarget + **placementIndex**;
  removals; replaces (swap); cut/fit changes; furniture BBs (v2); hut-chest material changes (inventory slice).
- **Derived locally, deterministic given the same blocks and indices [I]:** HousePart meshes, cuts, roofs, trims, colliders, navmesh
  (`HouseMeshWorkers`, `HousePart` drain). The game itself rebuilds all of this from the spec list on load, which is the basis for this assumption.
- **Local only (never sync):** build mode on/off, ghost/preview, snapping and aim, compass, highlights, prompts, each player's undo stack,
  each player's `PlayerBBCollection` plans and stock, player blueprint library, selected card.
- **Optional (v2):** blueprint *sites* (laid-out ghosts, `BlueprintSiteData`). Pieces built from a site replicate as ordinary placements.

## 3. Proposed sync design (fits CoopSession: host-authoritative, host relays)
**Identity:** `placementIndex` (int) for house blocks. The host is the only allocator of canonical indices. HousePart has no id on the wire:
parts are found or created from the spec's `refPosition/refRotation` [I].

**Flow (optimistic origin, host confirms):**
1. The local player builds normally: game validation, payment from their own backpack or hut chests, VFX and undo all run locally.
2. A postfix observes the commit and sends `BuildOp` to the host (a client sends it as a request; the host handles its own edits directly).
3. Host: re-checks (typeID resolves, index not already in use, `CommitBlockList(list, AllOrNothing)` succeeds in a free-pay scope),
   assigns a canonical placementIndex, applies, and broadcasts `BuildApplied` to everyone, origin included.
4. Origin: if the canonical index differs, `HouseBBPlacement.RestorePlacementIndex(hbo, canonical)` + `ReservePlacementIndex`. Others apply.
   On `BuildRejected` the origin removes its piece with `RemoveBBFromWorld(go)`, which refunds it [I].
- **Apply guard:** a static `BuildSync.Applying` flag set around remote applies. All observer patches return early while it is set.
  Undo registration is skipped too: prefix `RegisterStep_*` returns false while Applying, so remote pieces never enter local undo stacks.
- **Free-pay scope while applying:** prefix `BB_Building.LetPlayerPayForBB` → return true, and `HutBuildMaterials.TryTake` → true without taking.
  Better: use `SpawnHouseBlockInHome(ref spec, partPose, index)` directly, if it turns out not to pay [I, test].

**Messages** (ids continue from 17; Protocol.Version → 4; all `DeliveryMethod.ReliableOrdered`; event-driven, no fixed rate;
a drag or blueprint fill becomes one message holding N blocks, capped at `HouseBlockList.MAX_BLOCKS`):
| id | name | dir | body |
|---|---|---|---|
| 18 | BuildRequest | client→host | `u16 reqId, byte op, payload` |
| 19 | BuildApplied | host→all | `u32 seq, i32 originPlayer, u16 reqId, byte op, payload (canonical indices)` |
| 20 | BuildRejected | host→origin | `u16 reqId, byte PlacementRefusal` |
| 21 | HouseSnapshot | host→client | `u32 seq, i32 lastPlacementIndex, u16 n, n × Block` (join and resync) |
| 22 | HouseHash | either | `u32 seq, u64 hash(sorted (index, typeID, quantized house-local pose))` (drift check every ~10 s and on join) |

`op`: Place(N blocks) | Remove(N indices) | Replace(index, typeID) | CutSettings(index, cut) | FitTarget(index, int) | (v2) Furniture*.
`Block` ≈ 90 B: `string typeID` (or u16 index into `PossessionTypeReference.possessionIDs` once both sides agree), `Vector3 refPos, Quat refRot`
(hut-local part pose), `Vector3 blockLocalPos, Quat blockLocalRot`, `byte flags (isDiagonal, keepWhole, reversed)`, `Vector3 diagonalForward`,
`Vector3 boardDirection`, `byte braceOverrides, u16 removedExtras, u8 nRefused + i32[] refusedCutters`, `i32 fitTarget`, `i32 placementIndex`.
The current hut (71 blocks) is about 6.5 KB, which LiteNetLib fragments fine on reliable channels.

**Harmony hook points** (all on `BB_Building` unless noted; choose after the observation test in step 5):
- Place: postfix `HandlePlacedHouseBB(GameObject, Nullable<HouseCutSettings>)` (skip if `SaveLoadBBs.isloading` or Applying). Then
  `HouseBlockSpec.FromPlaced(hbo, placedBBs[go], BB_Building.Home)`. Alternative: postfix `BB_Building_UndoRedo.RegisterStep_Place`.
- Remove: prefix `UnregisterRemovedBB(GameObject)` (read `HouseBuildingObject.placementIndex` before teardown), or `RegisterStep_Remove`.
- Replace: postfix `RegisterStep_Replace(bbOld, bbNew, go)` (also covers `SwapHouseBlockPaid`).
- Cut and fit: postfix `RegisterStep_Settings(bb, go, old, new)` and `RegisterStep_FitTarget(bb, go, old, new)`.
- Undo and redo: postfix `BB_Building_UndoRedo.ExecuteStep(Step, bool isUndo)` if the funnels above don't already see them
  (undo recreation goes through `RecreateHouseBlock` and `SpawnHouseBlockInHome` [I]).
- Apply remote: Place → `SpawnHouseBlockInHome(ref spec, new Pose(refPos, refRot), placementIndex)` (fallback `SpawnHouseBlock(ref spec)` +
  `RestorePlacementIndex`). Remove → `RemoveHouseBlockUnrecorded(go)`. Replace → remove + spawn with the same index and the new card.
  Cut → `cutManagerTool.ApplyCutSettings(hbo, cut)` or `hbo.SetCutSettings` + `HouseBBPlacement.SortAndMarkNeighboursDirty(hbo)`.
  Lookup by index: scan `BB_Building.placedBBs` keys → `GetComponent<HouseBuildingObject>().placementIndex` (cache a dictionary,
  invalidate on op).
- Initial sync and resync: v1 relies on both peers loading the same save (current dev flow) plus a `HouseHash` check. v2: the host sends
  `HouseSnapshot`, and the client applies `SaveLoadBBs.DeleteCurrentState()` + `LoadSpecificHouse(savedBBs)` (built from the snapshot), or writes XML and calls
  `SaveLoadCustomHouse.LoadHouseFromPath`. Only the `placedBBs` and `houseParts` sections should be filled. Leave `unlockedAndOwnedBBs` and
  station inventories at the client's current values.

**Resources (ties into inventory):** v1 has the origin pay locally through the game's normal flow, and the host applies for free. Caveat:
`HutBuildMaterials` takes from **hut chests**, which are shared world objects. Until the inventory slice syncs chests, each peer's chests
diverge after builds. Long-term: the host owns chest contents, a client build draws from its backpack plus host-confirmed chest takes,
and refunds (`Refund{items, money, givenTo}`) go to the remover.

## 4. Risks and open questions (need a runtime test)
1. Which hooks fire for single place, drag (`CommitDrag`), blueprint fill (`CommitBlockList`), copy-paste (`PlaceCopyNow`), and undo/redo?
   IL2CPP may inline small callees, so a postfix on an inlined method never fires.
2. Does `SpawnHouseBlockInHome` / `SpawnHouseBlock` pay, check plans, register undo, or fire `OnPlacedBB`? Does it create the HousePart
   from `refPosition/refRotation`, or need an existing one? Does it work outside build mode, or when the remote player's hut is elsewhere?
3. Is `HouseBlockSpec.FromPlaced(..., BB_Building.Home)` the same frame `SpawnHouseBlockInHome` expects (`partInHome`)? Compare by
   placing on one peer and round-tripping the spec on the same peer.
4. Do the parts merge and regenerate identically on both peers (same mesh hash) when pieces arrive in a different order or with a
   corrected placementIndex? Changing the index after spawn may need `SortAndMarkNeighboursDirty`.
5. `LoadSpecificHouse` vs `LoadData`: what does each overwrite (plans/stock, chests, blueprint sites, Baba position)? Is it safe mid-game
   with players standing on the hut (`ChildPlayerWhenStandingOnHouse`)? Does `DeleteCurrentState` refund pieces to the player?
6. Remote removal of a piece that sits in a local undo stack: undo may try to recreate it (`IsRecreateImpossible`, `IsPlaced` exist [V]).
   This may need a prefix that drops steps whose instance is gone.
7. Simultaneous edits of the same cell: the host-side check must reject the second one. Does `CommitBlockList(AllOrNothing)` report
   `WallSlotTaken` / `SameTypeSameSpot` without a ghost?
8. Supports: removing a floor probably cascades to children (`RecursivelyStoreChildrenBBs`). Each peer must cascade the same way, or the host
   sends the full removed set.
9. Plans are per player. If the host applies a card the host has never unlocked, `PlayerBBCollection` counters (`CountPlaced`,
   `categoryInstancesOwned_placedOnly`) may go negative or trigger unlock/quest logic (`ScanHouseUnlockScore`, tutorial monitors).
10. `cantRemoveInTheseChapters` and `StoreChapterLocked` depend on chapter progress. Clients must not remove what the host's chapter forbids.
11. Furniture/stations (chests, cauldron, plants) have per-instance state keyed by the unstable gameObjectID. They need their own id scheme (v2).

## 5. First implementation step (small, observation only)
Add `Sync/BuildObserver.cs` (no network), enabled with a `-coopbuildlog` flag:
- Postfixes (log only) on `BB_Building.HandlePlacedHouseBB`, `TryCommitSpec`, `CommitBlockList`, `SpawnHouseBlock`, `SpawnHouseBlockInHome`,
  `UnregisterRemovedBB`, `RemoveHouseBlockUnrecorded`, and `BB_Building_UndoRedo.RegisterStep_*` / `ExecuteStep`. Each logs frame,
  `SaveLoadBBs.isloading`, typeID (`SaveLoadBBs.BBIDConverter.TypeToID`), `placementIndex`, and the `HouseBlockSpec.FromPlaced(..., BB_Building.Home)` fields.
- A dev command logs `HouseHash` and the block count on both copies after load. The hashes should be equal with the same save.
- Round-trip test on the host alone: capture the spec of the last placed piece, `RemoveHouseBlockUnrecorded`, then `SpawnHouseBlockInHome` with the
  same spec and index. Compare hash and screenshot.
That test answers risks 1–4 and decides the hook set. Step 2 after that: one-way host→client `BuildApplied` for Place/Remove,
then client requests.
