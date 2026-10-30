# Puppet gear, sounds, cloth, IK: research (2026-10-26)

Sources: interop `Assembly-CSharp.dll` + `Emberstorm_CodeLibrary.dll` (dumper), host/client `LogOutput.log` and
`player-*.log`, `global-metadata.dat` strings. No runtime run. **[V]** = verified from signatures/logs,
**[I]** = inferred (method bodies are stubs; behaviour is a guess from names/fields).

## 1. Game classes, fields, methods

Ownership:
- `UseItemManager` (singleton, `static Instance`) [V]: `BaseUseItem currentItem`, `BaseUseItem inLeftHand`,
  `BaseUseItem prevItem`, `Backpack backpack`, `List<BaseUseItem> allItems`, `bool isCarryingObject`,
  `bool currentlySwapping`, `bool useItemsBlocked`; `void SetCurrentItem(BaseUseItem)`, `void SetCurrentItem(string id)`,
  `BaseUseItem ItemOfType(BaseUseItem.ItemType)`, `BaseUseItem GetItemByID(string)`, `void EquipNone()`,
  `void UnequipAllItems(bool removeImmediately)`, `void OnStartCarrying(CarriableObject)`, `void OnStopCarrying(Vector3)`.
  `inLeftHand` separate from `currentItem` suggests the lantern can be held in the left hand alongside another item [I].
- `BaseUseItem : MonoBehaviour` [V]: `string ID`, `ItemType Type`, `bool isPhysicalTool`, `bool stashInBackpack`,
  `Backpack.SlotSize backpackSlotSize`, `bool seperateBackpackTool`, `GameObject physicalTool`, `GameObject backpackTool`,
  `PhysicalToolSlot physicalToolSlot` (LeftHand=0, RightHand=1, Head=2), `float holdToolAnimationWeight`,
  `ObjectFollower handFollower / headFollower / backpackSlotFollower`, `string carryToolAnimationBool`,
  `FMODUnity.StudioEventEmitter equipSound`, `bool unlocked`, `bool IsEquipped`.
  Methods: `virtual void OnEquip()`, `virtual void OnUnequip(bool layerWeightTo0, bool putInBackpack)`,
  `StartCarryToolInLeftHand(float leftHandWeight)`, `StartCarryToolOnHead()`, `StopCarryToolInLeftHand(bool,bool)`,
  `StopCarryToolOnHead(bool putInBackpack)`, `PutAway(bool)`, `StashInBackpack()`, `RemoveFromBackpack()`,
  `SetPlayerLeftHandAnimLayerWeight(float)`, `SetPlayerLeftArmRig(float weight, float speed, Vector3 targetPos, Vector3 targetRot, Vector3 hint)`,
  `SetPlayerAnimBool/Trigger/Float(string, ...)`.
- `BaseUseItem.ItemType` [V]: None 0, Birds 1, Lantern 2, TeleportCharm 3, PineBranch 4, Fire 5, SvarogCharm 6,
  WateringCan 7, GardenMode 8, BuildMode 9, EnchantedLarchCone 10, BrokenDziadyMask 11, GenericWateringCan 12,
  TalkToAnimalsPotion 13, StrengthPotion 14, HouseBuildingMode 15.
  Subclasses [V]: UseItem_Backport, _Birds, _BrokenDziadyMask, _BuildMode, _EnchantedLarchCone, _Fire, _GardenMode,
  _Lantern, _None, _PineBranch, _SvarogCharm, _WateringCan.
- **There is no axe** [V]: no type/field/string matching Axe/Hatchet in Assembly-CSharp or metadata. Tree chopping is
  a bird ability (`UseItem_Birds.EnableTreeChopping`, `static bool canDamageTrees`, `GameEvents.Player.Abilities.EnabledTreeChopping`).
- `UseItem_Lantern : BaseUseItem` [V]: `ToggleThisGameObjectHelper lightObject`, `LightDaytimeAdjustor lanternLight`,
  `VisualEffect vfx`, `UnityEvent OnRaiseLantern / OnLowerLantern / OnPutLanternOnBackpack / OnLanternIsRemovedFromBackpack`,
  `Transform rotatingBone` (swing), `StudioEventEmitter oilLampMovement`, `float upWeight, upSpeed`,
  `Vector3 upTargetPos, upTargetRot, upHintPos` (left-arm IK pose when raised), `bool clicked`;
  overrides `OnLeftClick/OnLeftRelease/OnRightClick/OnEquip/OnUnequip/Update`.
  Lit state = `lightObject.gameObject.activeSelf` [I] (`ToggleThisGameObjectHelper` (Emberstorm_CodeLibrary):
  `TurnOn()`, `TurnOff()`, `Toggle()`, `ChangeState(bool on)`, delayed on/off [V]).
  Raised state: static `GameEvents.Player.UseItems.OnRaisedLantern / OnLoweredLantern / OnEquip_Lantern` (Il2CppSystem.Action) [V].
- `LightDaytimeAdjustor : MonoBehaviour` [V]: `HDAdditionalLightData l`, `Light _legacyLight`, `TransformReference weatherManager`,
  presets, flicker, a global light/shadow budget (`static List<LightDaytimeAdjustor> s_heldBuffer`, `_budget*`, `_shadow*`).
- `Backpack : MonoBehaviour` [V]: `List<Backpack.ToolSlot> toolSlots` (`Transform t`, `Transform storedObject`,
  `SlotSize slotSize`, `int frameStored`), `bool currentlyDisabled`, `List<Transform> toReenable`;
  `bool TryStoreItemInSlot(Transform toStore, ObjectFollower of, SlotSize)`, `bool TryRemoveItemFromSlots(Transform)`,
  `bool ItemAlreadyStored(Transform)`, `DisableBackpack()`, `EnableBackback()` (sic). SlotSize: Small, Medium, Large, HangingLarge.
  Also referenced as `PlayerMovement_2021.backpack` and `UseItemManager.backpack` [V].
- `PlayerTool : MonoBehaviour` [V] (the "Tools" in hand that aren't use-items): `PlayerTool.Type toolType`
  (PineStick 0, SvarogCharm 1), `ObjectFollower handFollower, backpackSlotFollower`, `string animationBool`,
  `MeshRenderer meshRenderer`, `VisualEffect castVfx`, `bool isAbleToCastSpells`, `SetAbleToCastSpells(bool)`.
  `PlayerMovement_2021`: `PlayerTool pineStick, svarogCharm, heldTool`, `PlayerTool.Type holdingTool`,
  `bool stashToolInBackpack`, `Transform rightHandParent`, `StopHoldingItem(bool stashInBackpack)`,
  `StartHoldingItemAnimEvent()`, `StopHoldingItemAnimEvent()`, `HandAtHead()` [V].
  Static events `GameEvents.Player.StartHoldingItem : Action<PlayerTool.Type>`, `StopHoldingItem : Action<bool>` [V].
- `ObjectFollower : MonoBehaviour` (how every tool/backpack follows the body; nothing is parented to bones) [V fields, I behaviour]:
  `bool followTargetFromTransformReference`, `TransformReference followTargetTransformReference`, `GameObject followTarget`,
  `bool followPosition, followRotation`, `Vector3 positionOffset, positionLocalOffset, rotationOffset`,
  `bool maintainInitialOffset`, `bool useDrag`, `Vector3 drag`, `bool onlyFollowIfEnabled`, `float executionOrder`;
  `Register()`, `SetFollowTargetTransform(Transform)`, `ApplyPosition()`.
- `TransformReference : ScriptableObject` (Emberstorm_CodeLibrary) [V]: `Transform transform`, `Action onChange`,
  `AssignTransform(Transform)`; filled by `TransformReferenceAssigner : MonoBehaviour` (`TransformReference toAssign`,
  `Assign()`, `Unassign()`, `unassignOnDisable`). These are **global singletons**: any clone that keeps an assigner
  steals the reference; any clone that keeps a follower follows the *local* player [I, strongly].
- Carrying [V]: `CarriableObject : CompletableStation` (world object; `CarriableObjectType type` None/Chicken/Egg/Embers/
  Pumpkin/Cabbage/LifelessLutki/SpiritJar/SpiritJar_withZlyden/SpiritJar_withBludnik/DBJ, `bool isHeavy`,
  `ObjectFollower objectFollower`, `Transform leftArmTarget, leftArmHint, rightArmTarget, rightArmHint`,
  `CaptureState(Transform poiRoot)`, `StartCarrying()`, `StopCarrying(...)`).
  `PlayerMovement_2021.StartCarryingObject(CarriableObject)`, `StopCarryingObject()`, `ApplyCarryArms()`, `bool IsCarryingObject`.
  Static events `GameEvents.Player.OnStartCarryingObject : Action<CarriableObject>`, `OnStopCarryingObject : Action<Vector3>`.
- Sitting [V]: `PlayerMovement_2021.SitOnChair(Transform)`, `StandUpFromChair()`, `SitDown(float timer)`, `StandUp()`,
  `bool isSitting, sittingOnChair`, `bool IsSittingAnywhere`; `PlayerSittingPlace` (`static string SitOnChairParam,
  BanyaCleanseParam` = animator param names, `StartSitting()`, `StopSitting()`); static `GameEvents.Player.SitDown/StandUp/SitOnChair/StopSitOnChair`.
- Footsteps chain [V types, I wiring]: animation clip event `OnFootFall(int index)` → `PlayerAnimationEventForward`
  (on the animator GameObject; fields `PlayerMovement_2021 _playerMovement`, `float _minimumSpeedForAudio`) →
  static `GameEvents.Player.OnFootDown : Action<int>` → `WalkingSounds.OnFootDown(int index)` →
  `WalkingSounds.PlaySound(EventReference eventRef, int groundType, int walkType, Vector3 pos)`.
  `WalkingSounds`: `Transform leftFoot, rightFoot`, `TerrainInfoScrob terrainInfo`, `EventReference footSteps, onJump, onLand`,
  `static bool DetermineFootType(int index)`, `static int DetermineWalkType(int index)` (index encodes foot + walk type),
  `OnJump()`, `OnLand()`, `RaiseBothFeetContact(GroundProbe, FootstepMotion)`.
  Ground: `TerrainInfoScrob.ProbeGround(Vector3 pos, float rayStartAbove, float rayLength) : GroundProbe`
  (`GroundType ground, Vector3 point, normal, bool onTerrain, onPath`). Footprints/particles: static
  `GameEvents.Environment.OnFootstepContact : Action<FootstepContact>` → `GroundContactFx.OnFootstepContact(FootstepContact)`;
  `FootstepContact.At(ref GroundProbe, FootstepCharacter, FootstepMotion, bool isRightFoot, Transform foot, float scale)`.
  NPCs use `NPCSound.OnFootFall(int)` directly (self-contained). `NonIKFootStepSounds` (`List<Transform> footTransforms`,
  `EventReference footstepEvent`, `stepHeight`, `boneToGroundOffset`, `Update`) detects steps from bone height, no events.
  Other animation-event receivers: `GenericAnimationSound.AnimationEventTrigger(AnimationEvent)`,
  `GenericAnimEventReceiver.AnimationEventTrigger(string)`, `PlayerAnimationEventForward.PlayAnimationVFX(string)`.
  `AnimationEventBlendGate.RaisingClipIsDominant(AnimationEvent, Animator)` (static) filters events from low-weight blended clips.
- IK / look [V]: `PlayerMovement_2021.OnAnimatorIK(int)` → `FootIK()`, `AimIK()`, `RightArmIK()`; `_leftFootIK/_rightFootIK`,
  `footIKHeight`, `SuppressFootIK(Object)`. Animation Rigging: `Rig aimRig, leftArmRig, rightArmRig`,
  `TwoBoneIKConstraint leftArmConstraint, rightArmConstraint`, `Transform rightTarget, rightHint`,
  `leftArmUseItemWeight/TargetPos/TargetRot/Hint`. `PlayerLookAt` (`ProceduralHeadRig _rig`, `Vector3 _point`,
  `_weightTarget`, `LateUpdate`). `ProceduralHeadRig : Il2CppSystem.Object` (plain class) with
  `bool BindHumanoid(Animator)`, `Bind(Transform head, Transform root, Vector3 headForwardLocal)`,
  `LookAt(Vector3 point, float weight)`, `StopLooking()`, `Apply(float dt)`, `Vector2 LookAngles`.
  `IKControlLeft/Right` exist (legacy, purpose on player unknown).
- MagicaCloth2 [V]: `CharacterAssembler.simulationParent`, `GetClothColliderComponentList(WearableMesh)`;
  `WearableMesh.hasSeperateClothSimulationMesh`, `clothSimulationPrefab`, `use{UpperLeg,LowerLeg,Hip,LowerBack,Chest,Neck,Head,UpperArm}Collider`.
  `MagicaDistanceCulling`, `AnimatorDistanceThrottle.SetClothPaused(bool)`. Log: host discovery saw
  `RekaCoop_Puppet_1/hair_VeryLongHair_01(Clone)/mesh_very_long_hair_1_Sim` → wearables spawned by ApplyLook *after*
  Strip do land on the puppet with their `_Sim` mesh [V]; whether a live MagicaCloth runs on them is unknown.

## 2. Per item: state + how to show on the puppet

General rule [I]: gear objects follow targets via `ObjectFollower`, so for puppets **clone the gear GameObject once,
strip it to Keep-list components (Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, LODGroup, Light,
HDAdditionalLightData, VisualEffect; optionally LightDaytimeAdjustor), destroy every ObjectFollower /
TransformReferenceAssigner / StudioEventEmitter / BaseUseItem / PlayerTool / Backpack, and parent it to the puppet's
matching transform** with a local pose. Instantiating from item definitions is not possible: physical tools are scene
objects referenced by `BaseUseItem.physicalTool/backpackTool`, not prefabs [I]. Clone under an inactive holder exactly
like `Puppet` does.

Matching transform: on the sender, the active follower's target `T` is resolved to a path relative to the body
(`Reka_Body_Base_01/...` bone) or to the Backpack root (slot `toolSlots[i].t`). Send the path hash + the gear's
pose relative to `T` (captured, so `ApplyPosition` semantics don't matter). Receiver maps hash → transform in the
puppet (puppet bones; puppet's cloned backpack slots). Hash table is built from the same prefab on both sides [I].

| Item | State to sync | Puppet display |
|---|---|---|
| Backpack | visible (`!backpack.currentlyDisabled` and active) | clone Backpack GO, parent to the bone its follower targets (spine/chest, find at runtime) |
| Each use-item (`allItems` with `isPhysicalTool`) | per object (`physicalTool`, `backpackTool`): active, anchor hash, local pose | clone each object lazily on first need; toggle active; reparent on anchor change |
| PlayerTools (pineStick, svarogCharm) | same as above (+ `isAbleToCastSpells` for glow) | same; `castVfx` optional |
| Lantern | lit (`lightObject.gameObject.activeSelf`), raised (event pair), location (via item row) | keep Light + HDAdditionalLightData + VisualEffect in clone; toggle light GO; raised pose needs arm IK (below) |
| Current / left-hand item | `currentItem.Type`, `inLeftHand?.Type` (bytes) | drives equip sound on change; also good for debugging |
| Carry | `IsCarryingObject` + `CarriableObjectType` | arms come from animator bool/state if any + `ApplyCarryArms` IK (stripped); the carried object itself is a world entity → world-sync slice |
| Sit | nothing extra: animator bools/states + position already synced [I]; test it | — |
| Tool-use anims | already covered by animator params + layer state hash [V for params]; **layer weights are not synced** (`SetPlayerLeftHandAnimLayerWeight`, `holdToolAnimationWeight`) | add per-layer weight to PlayerSnapshot |
| Look-at | head look point (or `HeadRig.LookAngles`) + weight | puppet-owned `new ProceduralHeadRig()`, `BindHumanoid(puppetAnimator)`, `LookAt` + `Apply(dt)` in LateUpdate [I] |
| Foot IK | none (receiver-side) | own `OnAnimatorIK` on the puppet with ground raycasts [I]; low priority |
| Left/right arm IK (lantern raise, carry, watering can) | arm rig weight + target pos/rot/hint (local space) | keep Animation Rigging components on the clone (RigBuilder, Rig, TwoBoneIKConstraint, MultiAim...) and drive weights/targets; targets must be inside the clone (check) [I] |
| Footsteps | none (receiver derives from animation) | see below |
| MagicaCloth | none | see below |

Footsteps on puppets [I, runtime test needed]:
- Puppet animator runs the same clips with `AlwaysAnimate`, so clip events `OnFootFall(int)` should still fire on the
  puppet; after Strip nothing receives them. No "has no receiver" message appears in `player-host.log` from a session
  where a puppet existed [V absence], so Unity is silent here (release player) or events don't fire — test.
- **Never keep `PlayerAnimationEventForward` on a puppet**: it raises the static `GameEvents.Player.OnFootDown`, which the
  local `WalkingSounds` plays at the local player's feet [I].
- Plan A: injected `PuppetAnimEvents : MonoBehaviour` on the puppet root with `void OnFootFall(int index)` (and
  `PlayAnimationVFX(string)` no-op to stay quiet). It probes ground at the puppet foot
  (`walkingSounds.terrainInfo.ProbeGround(foot.position, 0.5f, 2f)`), then calls the local
  `WalkingSounds.PlaySound(walkingSounds.footSteps, (int)probe.ground, WalkingSounds.DetermineWalkType(index), foot.position)`.
  Optionally `GroundContactFx.OnFootstepContact(FootstepContact.At(ref probe, Player, motion, isRight, foot, 1f))` for footprints.
  Whether Unity dispatches animation events to an Il2CppInterop-injected method is unverified.
- Plan B (no events): `NonIKFootStepSounds`-style bone-height detection in our own Update (or add the game's
  `NonIKFootStepSounds` to the puppet with `footTransforms` = puppet foot bones, `footstepEvent` = `WalkingSounds.footSteps`;
  loses ground-type params).
- Jump/land: play `onJump`/`onLand` when the snapshot's jump/landing bools rise (statics `ANIM_START_JUMPING`, `ANIM_LANDING`).
- Equip sounds: on gear change, play `item.equipSound.EventReference` at the puppet with
  `FMODUnity.RuntimeManager.PlayOneShot(ref, pos)` [I]. Lantern creak (`oilLampMovement`) and breathing: skip.
- Check whether the body GO carries `GenericAnimationSound` (self-contained, plays at its own transform): if so add it to
  `Keep` for free tool/foley sounds [I].

MagicaCloth [I, runtime test needed]:
- Strip currently destroys copied MagicaCloth + body cloth colliders. ApplyLook then spawns fresh wearables (hair `_Sim`
  meshes seen on the puppet [V]) whose cloth setup calls `GetClothColliderComponentList` against the puppet's bones —
  colliders gone → cloth either errors, runs without body collision, or is not set up.
- `CharacterAssembler.simulationParent` on the clone may still point at the local player's object if it lives outside
  `Reka_Body_Base_01` → puppet cloth simulated in the wrong space. Log its path on the puppet.
- Recommended: keep `MagicaCloth2.*` collider components (type names starting with `Magica`) in `Keep`, set the puppet
  assembler's `simulationParent` to a puppet-owned transform if it is foreign, and let ApplyLook rebuild cloth. If
  unstable, fallback: disable `MagicaCloth` on puppet wearables and show the `_Sim` / base mesh static.
- Count "Adding null Transform to TransformAccessArray" (MagicaCloth2 `ColliderManager.Register`) with and without
  puppets; host and client logs already have 53/61 at startup, so measure the delta.

## 3. Message design

Protocol → v4. Two changes:

A) Extend `PlayerSnapshot` (Sequenced, 15 Hz, unchanged path):
- `byte layerCount; byte[] layerWeight` (weight*255) — fixes left-hand/tool hold poses. Raise `Layers` to the actual
  `anim.layerCount` (log it; current hard-coded 3 may be short).
- `short lookYaw, lookPitch` (deg*100) + `byte lookWeight` from `PlayerLookAt.HeadRig.LookAngles` / `_weightTarget`.
- Later: `byte leftArmW, rightArmW` + left target pos/rot (local to body) when arm rigs are kept.

B) New `MsgType.Gear = 18`, ReliableOrdered, on change only (poll diff at 10 Hz) + full on `PlayerJoined` (host keeps
latest per player and replays, same as Appearance); host relays client→others exceptPlayerId:
```
int    playerId
byte   flags        bit0 backpackVisible, bit1 lanternLit, bit2 lanternRaised, bit3 carrying, bit4 toolCanCast
byte   currentItemType, leftHandItemType, carryType (CarriableObjectType), holdingTool (PlayerTool.Type, 255 none)
byte   n; n × { string gearKey   // "<BaseUseItem.ID>/p" | "<ID>/b" | "tool/<PlayerTool.Type>" | "backpack"
                byte  state       // 0 hidden, 1 shown
                int   anchorHash  // FNV-1a of path relative to Reka_Body_Base_01 or "BP:"+path relative to Backpack
                Vector3 localPos; Quaternion localRot (only if shown) }
```
~10 entries × ~40 B, only on change: negligible. Gear keys are strings so allItems order doesn't matter.

Receiver (`Puppet.ApplyGear`): lazily clone the local object for each key (find via `UseItemManager.Instance.GetItemByID`
/ `PlayerMovement_2021.pineStick` etc.), strip, parent to anchor, set pose, SetActive(state). Lantern light: toggle the
cloned `lightObject` GO.

Hook points (sender). Prefer polling (robust); use events only to cut latency:
- Poll in `PlayerSync.Tick`: `UseItemManager.Instance.currentItem/inLeftHand/backpack`, each tracked gear GO's
  `activeInHierarchy` + enabled `ObjectFollower` target, lantern light `activeSelf`, `PlayerMovement_2021.IsCarryingObject`.
- Static Il2CppSystem.Action subscriptions (no Harmony; `DelegateSupport.ConvertDelegate`, keep refs):
  `GameEvents.Player.UseItems.OnRaisedLantern/OnLoweredLantern`, `GameEvents.Player.OnStartCarryingObject/OnStopCarryingObject`,
  `GameEvents.Player.StartHoldingItem/StopHoldingItem` → set a dirty flag.
- Harmony, if needed: `UseItemManager.SetCurrentItem(BaseUseItem)` postfix (non-virtual). Avoid patching
  `BaseUseItem.OnEquip/OnUnequip`: virtual, subclasses override, IL2CPP patch only hits the base body [I].
- Receiver hooks: none. Puppet gear must not register with game managers (strip ObjectFollower before activating).

## 4. Risks / open questions (need a runtime test)

1. Hierarchy of `PlayerCameraSetup`: which GOs hold Backpack, LanternHolder, Tools, which components they carry, which
   `ObjectFollower`s are enabled and their targets (bone names). Discovery dump needed (extend `Dev/Discovery.cs`:
   component type list per child to depth 3, follower target paths, `toolSlots[i].t` paths).
2. Components on `Reka_Body_Base_01` itself (is `PlayerAnimationEventForward`, `GenericAnimationSound`, RigBuilder there?).
3. Do clip events fire on the stripped puppet, and can an injected MonoBehaviour receive `OnFootFall(int)`?
4. Animator layer count and which layers are script-weighted; does crossfade by fullPathHash work on those layers.
5. Cloned `LightDaytimeAdjustor`: joins the global light/shadow budget — may dim/steal shadow budget from the local
   lantern. Alternative: keep only Light+HDAdditionalLightData and copy intensity/range from the local lantern's light.
6. `ObjectFollower` drag/`maintainInitialOffset`: relative pose may wobble; sending pose on change only could freeze a
   mid-swing pose. Sample the pose when the follower state changes + 0.5 s settle.
7. MagicaCloth on puppets (above): `simulationParent`, colliders, warning count, CPU cost for N puppets.
8. Arm IK targets (`rightTarget/rightHint`, `leftArmConstraint` data) may live outside the body → keep-rig path may bind
   to local targets. Check before keeping Animation Rigging components.
9. Carried objects are world POI elements (`CarriableObject.CaptureState`, unique POI element IDs) — needs world sync;
   until then the puppet carries nothing visible.
10. Gear clones taken while the local player has the item in a different state (e.g. lantern unlit, tool hidden):
    renderers/light must be forced to the remote state after cloning.

## 5. Recommended build order

1. Discovery dump (risks 1, 2, 4): `PlayerCameraSetup` subtree components + follower targets, body components,
   `anim.layerCount` + layer names, puppet `CharacterAssembler.simulationParent` path, Magica component types on wearables.
2. Snapshot v4: layer weights (+ full layer count). Cheap, fixes held-item arm poses for everything at once.
3. Backpack on the puppet (static clone on its anchor bone; always visible unless disabled).
4. Gear message 18 + held tools/use-items (hand/head/backpack slots), lantern lit/unlit with light.
5. Footsteps (plan A, fallback B) + jump/land + equip sounds.
6. Look-at via `ProceduralHeadRig` (snapshot fields).
7. MagicaCloth on puppets (keep colliders, fix simulationParent) or explicit disable.
8. Arm IK (lantern raise, carry arms), then foot IK. Carry objects wait for world sync.
