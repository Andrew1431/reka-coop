# Slice: shared world objects + inventory + crafting

Sources: interop Assembly-CSharp.dll (dumper), host/client CoopSaves slot "- 1 -/1" (tutorial map; host and client
CurrentMap_c.xml are byte-identical after gunzip), global-metadata strings.
Tags: [V] = seen in a signature or save data. [I] = inferred, needs a runtime check.

## 1. Game classes owning this state

### Item model
- [V] `Possession : ScriptableObject` (displayName, icon, StackLimit, isPlayerPossession) -> `LootablePossession` -> `Item`
  (category, rarity, StackLimit, foodTag/foodValue, cantBeDeleted, asBB). Items are assets, not instances.
- [V] `PossessionStack { Possession _type; int _amount; }`, `ItemStack : PossessionStack { Item Type }`.
- [V] `PossessionTypeReference` (ScriptableObject): `string TypeToID(Possession)`, `Possession IDToType(string)`,
  `FindPossession(string)`, `GetAllItems()`. Save files store items as `SerializablePossessionStack { string _typeID; int _amount; }`.
  -> Item ids are strings and identical on every machine (same assets) [V signatures, I for equality].

### Inventory
- [V] `Inventory : Il2CppSystem.Object`: `List<PossessionStack> possessionsInInventory`, `validPossessionGroups`,
  events `OnChange/OnAdd/OnAddSingle/OnTake` (Il2CppSystem.Action). Ops: `Add(Possession,int,bool)`, `Take(Possession,int)`,
  `TryRemove(Possession,int)`, `TryRemoveAtIndex`, `AddAtIndex(PossessionStack,int)`, `Swap(int,PossessionStack)`,
  `TakeStack(int)`, `TakePortion(int,int)`, `GetCountOf`, `Has`, `Clear`.
- [V] `PhysicalInventory : Inventory` adds `int inventorySlots` (fixed slots, empty stacks fill gaps), `TryAdd`, `CanAdd`,
  `GetSpaceFor`, `FillWithEmptyStacks`.
- [V] `InventoryManager : MonoBehaviour` (one per scene, the player's backpack):
  fields `PhysicalInventory inventory_collected`, `PhysicalInventory inventory_crafted`, `Inventory excessInventory`,
  `InventoryCommunication comm_Inv`, `PossessionTypeReference pRef`, `static int INVENTORY_SLOTS`.
  Mutators: `int AddItemsToPlayerInventory(PhysicalInventory inv, Item toAdd, int amount, bool handleExcess)` (+3 overloads:
  `(Item,int)`, `(Item,int,bool)`, `(Inventory)`), `int TryAddItems(Item,int)`, `HandleExcessItems(Item,int)`,
  `AddToExcessQuietly(Item,int)`, `int TakeFromExcess(Item,int)`, `TryTransferExcessItems()`,
  `bool RemoveItemsFromPlayerInventory(PhysicalInventory,Item,int)` (+`(Item,int)`), `RemoveItemsFromPlayerInventoryAtIndex(Item,int,int)`,
  `RemoveAllItemsPlayerHasOfType(Item)`, `Delete(int index)`, `OnInventoryChanged()`,
  `ISaveable_GetStateForSaving(SaveGame)` / `ISaveable_RestoreSavedState(SaveGame)`.
- [V] `InventoryCommunication : ScriptableObject` = a delegate bus (`Func/Action` fields): AddItemStack, TryAddItems, AddItems,
  HasItems, RemoveItems, RemoveItemsAtIndex, OnItemsAdded, OnInventoryContentChanged, AddMoney/RemoveMoney/HasMoney/
  GetCurrentMoneyCount, excess helpers. Gameplay code calls these; InventoryManager wires them in `SubscribeToGameEvents(bool)`
  [I: the `_SubscribeToGameEvents_b__22_*` lambdas]. Hooking the InventoryManager methods catches all bus calls [I].
- [V] Save (Inventory.xml, `SaveGame/SaveableInventory`): `collectedInv` (20 slots), `craftedInv` (20 slots), `excessInv`
  (slots -1 = unbounded overflow), `int currentMoney`.
- "Equipment": there are no equipment slots in the inventory [V]. Tools are `BaseUseItem` subclasses (UseItem_Birds, _Lantern,
  _WateringCan, _BuildMode, _GardenMode, _Fire, ...) managed by `UseItemManager` (`currentItem`, `allItems`,
  `UnlockItem(string)`, `SetCurrentItem(string)`); saved in Progress.xml `serializableUseItems` as
  `SerializableUseItem { string id; bool unlocked; float remainingDurability; }` [V]. Clothes = Collection.xml `foundWearables` [V].
- Building blocks are a separate store: `PlayerBBCollection` (AquiredBBs: `BBStack { bb, hasPlan, amountOfInstances }`),
  `CollectibleBuildingBlock.Take()` [V]. Belongs to the housing slice.

### Storage containers
- [V] `InteractableInventoryLogic : Interactable, IHasInventory, ISaveablePOIElement`: `PhysicalInventory myInventory`,
  `bool isOwnedByPlayerAndInHouse`, `string _uniquePoiElementID`, `On3DInventorySlotClicked(int,bool)`,
  `OnPlayerInventorySlotClicked(ItemStack,int,bool)`, `TakeAllContents()`, `ReturnAllItemsToPlayerInventory()`, `OnInvChange()`,
  `SerializableInventory GetSerializedState()`, `RestoreSerializedState(SerializableInventory)`.
  Subclasses: `InteractableInventoryLogic_TakeOnly`, `RestrictedInteractableInventoryLogic`, `InteractableInventoryWithConverterLogic`.
- [V] Hut chests: `BB_Chest_SaveLoad { InteractableInventoryLogic chest; }` -> House.xml `chests` (`SaveLoadBBs/ChestData { int gameObjectID;
  SerializableInventory chestInventory; }`). Oven: House.xml `breadOvenInventories` (input 3 / output 100 / fuel 1 slots).
  `SaveLoadBBs/MultiInventoryBBData { int gameObjectID; List<SerializableInventory> inventories; }`.
- [V] `HutBuildMaterials` (static): `HutChests`, `GetAvailable(Item)`, `HasAll(List)`, `TryTake(...)`, `Give(...)`,
  `Store { Backpack, Chest, Overflow }` -> building already draws from backpack + hut chests + overflow.
- [V] `SharedChestInventory { PhysicalInventory chestInventory; }` + `SharedChestInventory_Communication.GetSharedChestInventory`
  (a game-side "shared chest"; usage unknown [I]).

### World objects (pickups, harvestables, chests, POIs)
- [V] `CollectibleID : MonoBehaviour` (a pickup/harvestable): `Item type`, `int amount`, `Inventory inventory`, `bool collected`,
  `bool spawnedWithEnvironmentGen`, `Action OnPluck`, `Action<CollectibleID> OnBecomeNotCollectibleForAnyReason`,
  `UnityEvent OnCollectAction`. Methods: `CollectResult TryCollectItem()` (`Collected/AlreadyCollected/NoSpace`), `CollectItem()`,
  `OnCollect()`, `OnIsCollectedOrDestroyed()`, `DestroyCollectibleExternal()`, `Dissolve()`, `int GetRunningNumber()`.
- [V] Collection path is via birds: `UseItem_Birds.CollectItem(CollectibleID)`, `BasicCollect()`, `Shoot()`;
  `UseItemInteract_BirdVariant.TryCollectItem(CollectProjectile)`, `OnCollect(Transform)`, `OnHitAndDestroy(Transform)`;
  `CollectProjectile.OnHitSpecificCollectible(CollectibleID)`, `IEnumerator TransferItemsToInventory(float)` (items arrive later).
- [V] `DestructibleObject` (trees, rocks...): `currentHitPoints`, `DamageObject(float)`, `DestroyObject()`, `OnTakeCollectible(CollectibleID)`,
  `List<CollectibleID> collectibles`, `_uniquePoiElementID`, save states `SAVED_INTACT / _DESTROYED_WITH_COLLECTIBLES_LEFT / _DESTROYED_AND_COLLECTED`.
- [V] `TreasureChest : CompletableStation`: `StartLooting()`, `FinishOpening()`, `Loot()`, `MarkLooted()`, `bool looted`,
  `POI_Loot loot`, `CaptureState/ApplySavedState`. `POI_Loot : CompletableStation`: `GiveToPlayer()`, `GiveItemsToPlayer()`,
  `CalculateLootFromLootTable()` (random roll [I: diverges per machine]), `notCollectedItems/collectedItems`.
- [V] `CompletableStation`: `bool isCompleted`, `SetIsCompleted(bool)`, `_uniquePoiElementID` - base of most POI interactables.
- [V] `CarriableObject : CompletableStation`: `StartCarrying()`, `StopCarrying(Vector3,Quaternion)`, `isBeingCarried`.
- [V] `PlantGrowth`: `OnHarvest()`, `IsHarvested`, `Water()`, `SetStage(int)`, `_uniquePoiElementID`.
- [V] `Lootroom`, `LimitCollectibleYield.OnCollect(int)`, `AwardLootIfEveryChildIsCollected.OnChildCollected(CollectibleID)`.
- [V] Global events: `GameEvents/ExplorePhase.OnCollectibleCollectedOrDestroyed : Action<int>` (running number),
  `OnPOISolved : Action<string>`, `OnTreasureChestFound`; `GameEvents/Player/Abilities.OnCollect : Action<Item>`.
- Dropped items: no world "dropped item" entity exists [V: no such type; InventoryManager.Delete(int) deletes, overflow goes to
  `excessInventory`]. Throwing: `GameEvents/Player/Abilities.ThrowItem : Action<GameObject>`, `PlayerReforestation.ThrowableItem` (seeds/cones).

### Identity (the key question)
1. POI elements [V save + signatures]: `POI_SaveLoad { string ID; string uniqueID; List<POIElementIDData> poiElementsToSave }`;
   every saveable element implements `ISaveablePOIElement { string UniquePoiElementID; GetStateForSaving/RestoreSavedState(SaveableMap, poiId, elemId) }`.
   Save rows key on `uniquePoiID` + `uniquePoiElementID` + `debugName` (`SerializablePOIObject.FindInList(list, poiId, elemId, debugName)`).
   In this save: 589 element ids are prefab GUIDs, 77 are hierarchy paths like `Merchant_Cart/AI_Donkey#GeneralAIController`.
   49 element ids are reused across POIs (same prefab placed twice, POI id suffixed `_01`), so the element id alone is NOT unique;
   use the triple. POI uniqueIds come from the save (`SerializablePoiMetaInformation.uniqueId`) -> same on both machines when
   both load the same save [I, very likely].
2. Env-gen collectibles (bushes, berries, wood...) [V]: `static int ObjectDistributor.runningNumber`,
   `ObjectDistributor.TagWithRunningNumber(GameObject)`, `CollectibleID.GetRunningNumber()`, persisted as
   `SaveableMap.goneCollectibles : List<int>` via `TrackMapProgress.OnCollected(int)`. Positions are replayed from
   `SaveableMap.mapGenState` (`EnvironmentRandomState.serializableOTCStates` -> `SerTransform_OTC { pos, yRot, subBiomeIndex, objectIndex }`).
   Because the game itself relies on running numbers matching across save/load, they are deterministic for a given saved map [I].
   Risk: `BeginInstantiateAsync` - numbering order may depend on async completion [I]. Fallback key: item type + position rounded to 0.25 m.
   `ObjectDistributor.allSpawnedCollectibles : List<GameObject>` gives a lookup table [V].
3. Hut objects: House.xml `gameObjectID` ints (BBData, CauldronData, ChestData) [V]. Stable across machines only if both load the same House.xml [I].
4. New maps: `EnvironmentRandomState.InitializeWithDerivedSeed(GenStep, int masterSeed)`, `EnvironmentGenManager.useSeedOverride/seedOverride` [V].
   When the hut travels to a NEW map each machine generates on its own; unless the host's seed (or whole SaveableMap) is sent,
   POIs/collectibles will not match at all [I - biggest blocker, owned by the world-gen slice].

## 2. Deterministic vs must-sync

| Thing | Status |
|---|---|
| Item definitions, recipes (`P4_Recipe`, `P4_RecipeTypeReference`), loot tables | deterministic (assets) |
| Map layout, POI placement, collectible positions on a loaded save | deterministic from save [I]; new map = must-sync seed (world-gen slice) |
| Collectible gone set (running numbers) | must-sync, host-authoritative |
| POI element state (chest looted, station completed, destructible HP/destroyed, carried objects) | must-sync events, host-authoritative |
| `POI_Loot` random roll | must-sync result (host rolls, or host sends item list) |
| Container contents (hut chests, POI inventories, oven) | must-sync, host owns |
| Crafting queues (`CraftingBehaviour`) | must-sync, host owns; timers tick from host snapshot |
| Personal inventory / money / use-item unlocks | depends on inventory model below |
| Plants (`PlantGrowth` stage/water) | host owns; growth is time-based [I: DaytimeManager sync covers most] |

## 3. Proposed sync design (fits CoopSession)

Authority: host keeps `WorldState { HashSet<int> goneCollectibles; HashSet<string> doneElements; Dictionary<string,Inventory> containers; }`.
Clients never mutate shared state directly: they send intents, host validates first-come, broadcasts results. Host relays nothing raw.

Key string: `p:<uniquePoiID>/<uniquePoiElementID>#<debugName>` for POI elements, `c:<runningNumber>` for env collectibles,
`h:<gameObjectID>` for hut objects.

Messages (all ReliableOrdered; event-driven, no fixed rate; consider a separate LiteNetLib channel so PlayerState is unaffected):
- 18 `WorldClaim` client->host: `int seq, byte kind (Collect/Destroy/Loot/Complete/Harvest), string key`.
- 19 `WorldClaimResult` host->client: `int seq, bool granted, [loot: int n, (string typeId, int amount)*n]`.
- 20 `WorldEvent` host->all: `byte kind, string key, int byPlayer, [float hp]` - others apply silently
  (`CollectibleID.DestroyCollectibleExternal()`, `DestructibleObject.DestroyObject()` / set HP, `TreasureChest.MarkLooted()`,
  `CompletableStation.SetIsCompleted(true)`). Host replays the full gone/done set to late joiners on PlayerJoined (like Appearance).
- 21 `ContainerOp` client->host: `string key, byte op (Take/Put/TakeAll/Swap), int slot, string typeId, int amount, int seq`.
- 22 `ContainerState` host->all: `string key, int slots, (string typeId, int amount)*slots` (<= 100 slots, tiny). Sent after every change.
- 23 `CraftOp` client->host: `string stationKey, byte op (Queue/Cancel/Redeem), string recipeId|int slot`.
- 24 `CraftState` host->all: `string stationKey` + `SerializableCraftingBehaviourState` fields
  (queueCapacity, timeScale, timeUntilNextCraftCompleted, indexOfActiveRecipeInQueue, [recipeID,isStarted,isFinished]*).
- 25 `InventoryState` (only for the shared model): `int money, collected/crafted/excess snapshots`.

Harmony hook points (all [V] signatures; IL2CPP inlining may bypass some [I]):
- Pickups: prefix `CollectibleID.TryCollectItem()` - on a client, if key is known-gone return `AlreadyCollected` (skip original);
  otherwise run it (optimistic) and postfix sends `WorldClaim`. On denial client calls `InventoryManager.RemoveItemsFromPlayerInventory(type, amount)`.
  Also postfix `TrackMapProgress.OnCollected(int)` / subscribe `GameEvents.ExplorePhase.OnCollectibleCollectedOrDestroyed` as a catch-all.
  Bird path is delayed (`TransferItemsToInventory`): claim at `CollectProjectile.OnHitSpecificCollectible`, not at inventory add.
- Destructibles: postfix `DestructibleObject.DamageObject(float)` (send hp) and `DestroyObject()`.
- Chests/loot: prefix `TreasureChest.StartLooting()` - pessimistic (wait for grant; the `timeToOpen` lid animation hides RTT).
  Prefix `POI_Loot.GiveToPlayer()`/`CalculateLootFromLootTable()` on client: use the item list from `WorldClaimResult` instead of rolling.
- POI completion: postfix `CompletableStation.SetIsCompleted(bool)` -> WorldEvent (needs filtering; 127+ stations, many tutorial).
- Carry: postfix `CarriableObject.StartCarrying()` / `StopCarrying(Vector3,Quaternion)` (deny if other player carries).
- Containers: prefix `InteractableInventoryLogic.On3DInventorySlotClicked(int,bool)`, `OnPlayerInventorySlotClicked(ItemStack,int,bool)`,
  `TakeAllContents()` on clients -> ContainerOp, skip original; host applies, broadcasts `ContainerState`; clients write into
  `myInventory` via `RestoreSerializedState(...)` under a re-entrancy guard, then `UpdateUI()`.
- Crafting: prefix `CraftingBehaviour.TryToQueueUpRecipe(P4_Recipe,bool,bool)`, `InteractWithSlot(int)`, `KillRecipeInProgress(int,bool)`;
  host side postfix `GetDataToSave()` snapshot -> `CraftState`; client applies via `LoadSavedData(SerializableCraftingBehaviourState)`.
- Plants: postfix `PlantGrowth.OnHarvest()`, `Water()`.

### Inventory model options (open user decision)
A. Shared hut storage + personal inventory (recommended).
   - Personal: each machine's `InventoryManager` stays local; only pickup claims are synced. Hut chests, oven, cauldron queues = host containers.
   - Fits the game: building already pulls from Backpack + hut Chest + Overflow (`HutBuildMaterials`), crafting can include home inventories
     (`TryToQueueUpRecipe(..., includeHomeInventories)`).
   - Cost: medium. Needs ContainerOp/State (21/22), CraftOp/State (23/24), plus persistence of the client's personal inventory:
     client loads the HOST's save, so it starts with the host's backpack. Must store per-player `SaveableInventory` (+ money, use-items)
     in a sidecar (like appearance.xml) and apply via `InventoryManager.ISaveable_RestoreSavedState(SaveGame)` after load [I: method usable with a
     partial SaveGame]. Crafting that consumes client items needs client-side removal after host grant.
B. Fully shared (one backpack for everyone).
   - Host owns `inventory_collected/crafted/excessInventory` + money; every client mutation (all `AddItemsToPlayerInventory` overloads,
     `Remove*`, `Delete`, `TakeFromExcess`, `AddToExcessQuietly`, plus UI slot ops that hit `Inventory.Swap/TakeStack/AddAtIndex` directly)
     is forwarded; host broadcasts message 25 snapshots; client overwrites local inventory under a guard.
   - Cost: high-ish (many call paths; `Inventory` base methods are shared with chests so hooks must filter by instance; prediction/rollback
     for "has items" checks in quests, crafting, building). Persistence is free (host save). Weird UX: two players editing one 20-slot grid.
C. Fully separate (no shared storage).
   - Only the world gone/done set is synced. Cheapest: messages 18-20 only.
   - Hut chests and crafting stations then diverge per machine (each sees its own chest) unless made host-only; still needs the
     personal-inventory sidecar from A. Effectively A minus containers - a good stepping stone.

Recommendation: build C first (it is a strict subset), then add containers/crafting to reach A. Avoid B.

## 4. Risks / open questions (need a runtime test)
1. Do `GetRunningNumber()` values match on host and client for the same bush after loading the same save? After a map change?
2. New-map generation: is masterSeed random per machine? (expect yes -> world-gen slice must ship the host's seed or SaveableMap first.)
3. Does `DestroyCollectibleExternal()` remove without granting items or firing `OnCollectibleCollectedOrDestroyed`? (also updates host's gone list?)
4. Does Harmony on `CollectibleID.TryCollectItem` fire for bird collection, or does the bird path call `CollectItem` directly / inlined?
5. `POI_Loot` random results: is the roll seeded (same on both) or `UnityEngine.Random` (diverges)?
6. Composite key uniqueness: 5 duplicate (poi, element, debugName) triples in this save - check they are the same element listed in two lists.
7. Two players opening the same `InteractableInventoryLogic` UI at once; UI refresh after remote `RestoreSerializedState`.
8. Hut `gameObjectID` values: identical on both after loading the same House.xml?
9. Tutorial map (`isTutorial=true`, no `mapGenState`) may behave differently from generated maps - test both.
10. `InventoryManager.ISaveable_RestoreSavedState` with a SaveGame holding only `saveableInventory` - safe outside load?
11. Client autosaves: the client must not write the shared world back into its own save as truth (save isolation already redirects; decide policy).

## 5. First implementation step (small, diagnostic, no gameplay change)
`Sync/WorldObjects.cs`, both roles:
- Postfix `CollectibleID.CollectItem()` and `DestructibleObject.DestroyObject()`; log `GetRunningNumber()`, `type` id
  (`pRef.TypeToID`), `amount`, rounded world position, `spawnedWithEnvironmentGen`, and for POI elements the key triple.
- On scene ready, dump `ObjectDistributor.allSpawnedCollectibles` as `runningNumber -> (typeId, pos)` to `game/<copy>/collectibles.txt`.
- Test: autoload the same save in both copies, diff the two dumps (answers risk 1 and 9), bot-collect one bush on the client, check logs.
Then step 2: messages 18-20 for `c:` keys only (gone set + late-join replay, apply with `DestroyCollectibleExternal`).
