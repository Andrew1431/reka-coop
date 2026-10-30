# Slice: Ride the hut together

Static research only (interop signatures via dumper, metadata strings, save XML). Nothing launched.
Tags: **[V]** verified in a signature/data, **[I]** inferred (needs runtime confirmation).

## 1. Game classes owning the hut state

### ChickenLegsBehaviour : MonoBehaviour (the hut brain) [V]
- Refs: `Animator anim`, `Transform platformFollower`, `Transform chickenHouseRoot`, `ChickenLegsAIController aiController`,
  `Rewired.Player player`, `PlayerHouseInfo playerHouseInfo`, `Transform walkTarget`, `Terrain terrain`,
  `RuntimeTerrainFlattener runtimeTerrainFlattener`, `Rig leftFootRig/rightFootRig`, `Transform leftFootTarget/rightFootTarget`.
- State: `ControlMode CurrentControlMode` {None=0, PlayerControl=1, AIControl=2, LookAtTakeover=3}; `bool isStanding`,
  `IsStanding`, `_hutIsMoving`/`HutIsMoving`, `isBeingControlled`, `isAutoMoving`, `isStuck`, `walkTowardsTarget`,
  `float currentSpeed/targetSpeed/rotationSpeed`, `Vector2 moveAxis`, `CaptureHold CaptureHoldMode` {Off,Still,Greeting,WalkInPlace},
  `forbidStanding`, `overrideStanding`, `dontSitDownUnlessPlayerLeaves`, `playerAtHomeBase`, `lookAtEngaged`.
- Animator keys (statics): `ANIM_STANDING_BOOL`, `ANIM_STAND_UP_IMMEDIATELY_TRIGGER`, `kAnimStandingHash`,
  `kAnimSitDownImmediatelyHash`, `kStandUpStateHash`, `kSitDownStateHash`, `kSittingIdleStateHash`, `kStandingStillStateHash`;
  plus instance strings `lookAtAnimatorBool`, `playerSignedAngleFloat`.
- Loop: `Update()`, `LateUpdate()`, `TickMovement()`, `DetermineMoveParameter()` (reads input into moveAxis [I]), `Rotate()`,
  `MoveTowardsTarget()`, `EvaluateStandingState()`, `TickLookAtTakeover()`, `TickCaptureHold()`, `TickAnimatorTimeWatchdog()`,
  `RecomputeHutMovementState()`, `CheckIfHouseIsInAnim()`, `FootIK()`, `TickFootIK(...)`, `ClampMovementDelta(Vector3)`.
- Commands: `SetControlMode(ControlMode)`, `StartControlling()`, `StopControlling()`, `ToggleStanding()`, `StandUp()`,
  `SitDown()`, `SitDown(float visualDelay)`, `StandUpImmediately()`, `SitDownImmediately()`, `SnapToRestingPose(bool standing)`,
  `StandUpUntilRekaLeaves()`, `SetAnimatorStanding(bool, string)`, `SetWalkTowardsTarget(bool, string)`,
  `SetWalkTargetPosition(Vector3)`, `TeleportTo(Vector3, Quaternion)`, `TeleportToPositionAndSnapOntoTerrain(Vector3)`,
  `TurnHouseAroundAndPutItBackOntoTheMap()`, `StartOverride()/StopOverride()`, `SetCaptureHold(CaptureHold)`.
- Calling: `CallResult TryCallTo(Vector3)` {Coming, WontBudge, NoWayToPlayer, Ignored}, `CheckCall(Vector3, out Vector3)`,
  `SendCall(Vector3)`, `ShowCallRefusal(CallResult)`.
- Sit side effects (world-mutating, local): `FlattenTerrainAtPos()`, `SetSitDownFlattenPos(Vector3)`,
  `ApplySitEnvironmentChanges(int, string)`, `HideSitCandidates*()`, `ShowHiddenProps()`, `RebakeWorldNavMeshUnderProps(List)`,
  `EnvironmentSittingChanges(bool)`, `OnSitDownComplete()`, `OnStandUpComplete()` (anim events [I]).
- Feet: `LeftFootTouchGround()/RightFootTouchGround()/LeftFootLift()/RightFootLift()` (anim events [I]), `Action OnLeft/RightFootTouchGround`.
- UnityEvents: `OnStandup`, `OnSitDown`, `OnStartControlling`, `OnStopControlling`.
- Save: `GetStateForSaving(SaveableMap)`, `RestoreSavedState(SaveableMap)`, `LoadDataAfterEnvGen()`,
  `static CapturePlayerAboard(SaveableMap, Transform platform, Transform player)`, `PlayerRidesPlatform(Transform)`,
  `PlaceLoadedPlayerAboard()`, `Transform PlayerRoot`, `AwaitingLoadedPlayerAboard`.

### ChickenLegsAIController : GeneralAIController [V]
`ChickenLegsBehaviour chickenLegs`, `Transform aiTargetTransform`, `NavMeshPath _callPath`, `Vector3 _queuedCall`,
`IsAIDriving`, `HasQueuedCall`; `CallToPosition(Vector3)`, `QueueCall(Vector3)`, `TryFindReachableCallTarget`,
`SetTarget(Transform)`, `ApplyMovement()`, `Update()`, `LateUpdate()`, **`OnAnimatorMove()`**,
`ApplyRootMotionDuringPlayerOrLookAt(bool)`, `PrimeRootMotionForPlayerControl()`, `SyncAgentToCurrentPosition()`.
Strings "OnAnimMoveDeltaPos/ApplyRootMotion" diagnostics → hut is **root-motion driven** in all modes; the AI controller sits on
the Animator GameObject and applies the deltas [I from OnAnimatorMove + diag field names].

### Who drives it [V names, I flow]
- Player steering: sit on `HutControlPlayerSittingPlace : PlayerSittingPlace` → `GameEvents.Home.StartControllingLegs` →
  `StartControlling()` → mode PlayerControl, input from `Rewired.Player player` in `DetermineMoveParameter` [I].
  Camera: `ThirdPersonCameraController.isControllingChickenLegs`, `OnStartControllingLegs/OnStopControllingLegs`, `HutCameraDirector`.
- Calling ("whistle"): `UseItem_Backport.CallHutToPlayer()` (right-click on the backport item; `CALL_HUT_ANIM_BOOL`) →
  `static Func<Vector3,CallResult> GameEvents.Home.TryCallHut` → `ChickenLegsBehaviour.TryCallTo` → `SendCall` →
  `aiController.CallToPosition` (NavMesh pathing) [V names; chain I]. Left-hold on the same item = backport (teleport player home).
- Interaction prompts: `ChickenLegsInteract.StartControlling()/ToggleStanding()/OnOrderGiven()`.
- LookAtTakeover: hut turns to face a nearby player (`TickLookAtTakeover`, `lookAtTakeoverDistance`, `playerSignedAngleFloat`).
- Scripted: `TutorialChickenLegsManager` (`ParkLegsOffMap`, `GetChickenLegsAndStandUp`, `EnableChickenLegs`),
  `TeleportChickenLegsToHere.Teleport()`, `HouseAndPlayerOntoTerrainMover`, `BeyondEdgeOfMapHouseTravelTrigger` (map travel).
- Static event bus `GameEvents.Home` [V]: `ToggleStanding`, `SitDown`, `StartControllingLegs`, `StopControllingLegs`,
  `OnHutStandingChanged(bool)`, `OnHutMovementStarted/Ended`, `OnHutMoves`, `TeleportToPosition(Vector3)`,
  `TeleportToPositionDontSnapOntoTerrain(Vector3,Quaternion)`, `SetMoveTarget(Vector3)`, `SetFeatherColor(int)`,
  `OnPlayerBoardedHut`, `OnPlayerLeftHut`, `TriggerHouseJump/OnHouseJump`, `OnHutSitSpotChanged(Vector3,Quaternion,bool)`.

### Riding / parenting [V]
- **Current system is carry, not parenting**: `PlayerMovement_2021._platformCarry : PlayerPlatformCarry`
  (`Collider _ground`, `Matrix4x4 _groundWorldToLocal`, `Quaternion _groundRotation`, `CarryWithLastGround()`, `IsRideable(Collider)`,
  `LateUpdate()`, `static TELEPORT_DISTANCE`, `AIRBORNE_GRACE_SECONDS`). Each LateUpdate it moves the player by how its ground
  collider moved [I from fields].
- `PlayerHutBoardDetector` (`Init(PlayerMovement_2021, PlayerPlatformCarry)`, `Transform HutPlatform`, `HutHouseBounds _hutBounds`,
  `static bool PlayerAboard`, `SetAboard(bool)`, `IsHutGround(Collider)`, `LEAVE_GRACE_SECONDS`) → `PlayerMovement_2021.BoardHut()/LeaveHut()`,
  `_aboardHut`, `GameEvents.Home.OnPlayerBoardedHut/OnPlayerLeftHut`, `ThirdPersonCameraController.BoardHutCam()/LeaveHutCam()`.
  `PlayerAboard` is a **static** → local-player only.
- `ChildPlayerWhenStandingOnHouse` (`Transform playerObject, hut`, `PlayerMovement_2021 playerMovement`, `bool playerIsChilded`,
  `Update`, `CheckIfPlayerIsOnHouse`, `Child()`, `UnChild()`) still compiled (Assets/Scripts root, not Player/) — likely legacy,
  superseded by PlayerPlatformCarry [I]. Runtime check whether an instance exists/enabled.
- `PlayerHouseInfo : ScriptableObject` (`isPlayerChilded`, `isHouseInAnimation`, `isPlayerSittingInHouse`, `movementForbiddenByHouseAnim`)
  = shared asset singleton for the local player [V fields, I usage]. Puppets must never write it.

### Interior vs exterior [V] (all local-player presentation; no sync needed)
`PlayerHouseIndoorDetector` (ray enclosure → HDRP Volume weight, `_isIndoors`), `CheckIfPlayerIsInChickenHut` (camera under roof),
`CheckIfPlayerIsSittingInHouse`, `FMODEnterHouseSetter`, `HouseMusicManager`, `PlayerMovement_2021._insideHouse`,
`ThirdPersonCameraController._insideHouse/_aboardHut`, `GameEvents.Home.OnHutEnclosureChanged(float)`.
Interior floor and porch are both "hut ground" for the carry [I] → one hut-local frame covers both.

### Hierarchy [I, from field names + strings]
`ChickenLegs root (ChickenLegsBehaviour; scale 0.85 in save)` → `Animator` GO (+`ChickenLegsAIController`, root motion) →
leg skeleton (foot bones, IK rigs) → floor bone → **`platformFollower`** (string: "Per-frame movement ... of the floor bone
(ChickenLegsBehaviour.platformFollower)"; "is child of platformFollower (home)" → placed house BBs are children of it;
`SaveLoadBBs.platformFollower` GameObject) → `chickenHouseRoot`/house parts. `HutHouseBounds.PlatformRoot`, `PlayerHutBoardDetector.HutPlatform`
probably = platformFollower. The floor bobs with the gait, so **platformFollower, not the root, is the riding frame**.

### Save data [V, from decompressed CurrentMap_c.xml (gzip) = SaveableMap]
`chickenLegsTransform{pos,rot,scale}`, `hutPositionIsWorld`, `isControllingHut`, `hutIsStanding`, `hutStandingOverridden`,
`hutParked`, `hutStandsIdle`, `playerOnHut`, `playerPosOnHut` (platform-local!), `playerYawOnHut`. The game itself already
stores the rider in platform-local coords → good precedent for our wire format.
**Both current test saves (host + client + local/backup) have `hasCompletedChickenLegsRitual=false`, `hutParked=true`, hut at
(0,4.4,0)** → the hut is not unlocked yet in any test save.

## 2. Deterministic vs must-sync
| Item | Class |
|---|---|
| House blocks on the hut | same save → same on load [I]; building changes are another slice |
| Hut root pose (pos, rot) | **must sync** (root motion + NavMesh + input + per-frame physics depenetration) |
| Gait/stand/sit animation | **must sync** (animator params + per-layer state hash/time, as PlayerSnapshot) |
| platformFollower pose | derived from root + animator [I]; sending it too is cheap insurance for riders |
| Foot IK, foot VFX/FMOD, feather SMRs | local, deterministic from animator + terrain [I] |
| Stand/sit transitions, sit flatten/prop hide/navmesh rebake | event → replay locally (world-mutating; must not drift) |
| Feather colour (`SetFeatherColor(int)`) | must sync (event) |
| ControlMode, isStanding, isStuck | host-only logic; send as flags for UI/puppet |
| Indoor/roof/camera/music | per-player local, no sync |
| Rider position | must sync, in platform-local frame |

## 3. Proposed design (fits CoopSession)
**Authority**: host owns the one hut. Client keeps its own loaded hut object (it is the same house from the same save) but
turns it into a **kinematic mirror**: no AI, no input, no root motion, transform written from host data. Rejected alternative:
destroy client hut and clone the host one → lose house BBs, colliders, navmesh home, PlayerPlatformCarry targets. Rejected:
sending only inputs and letting client simulate → NavMesh/depenetration/root motion diverge quickly.

**Messages** (bump `Protocol.Version` to 4):
- `HutState = 18` host→clients, `Sequenced`, 20 Hz while standing/moving, 2 Hz at rest:
  `Vector3 rootPos, Quaternion rootRot, byte flags` (standing, moving, controlled, stuck,
  mode 2 bits), `float currentSpeed`, animator block identical to PlayerSnapshot (floats by index, bools bitmask,
  `StateHash[]/StateTime[]` per layer — reuse PlayerSync.CacheParams/Capture on `ChickenLegsBehaviour.anim`). ~120 B.
- `HutEvent = 19` host→clients, `ReliableOrdered`, also replayed to late joiners (like Appearance):
  `byte kind` {StandUp, SitDown, StandUpImmediately, SitDownImmediately, Teleport(pos,rot), FeatherColor(int), SitSpot(pos,rot)}.
- `HutRequest = 20` client→host, `ReliableOrdered`: `byte kind` {Call(Vector3 pos), ToggleStanding, RequestSteer, ReleaseSteer};
  later `HutSteer = 21` client→host `Sequenced` 20 Hz `Vector2 moveAxis, bool sprint` when host grants steering.
- `PlayerState` extension: `byte frame` (0 world, 1 hut-platform-local). If `PlayerHutBoardDetector.PlayerAboard` then
  `Position = platformFollower.InverseTransformPoint(body)`, `Yaw = body yaw − platform yaw`. Receiver interpolates in that
  frame and converts with **its own** platformFollower every frame (host: the real hut; client: the mirror). Frame switch =
  snap, no lerp across frames.

**Hook points** (Harmony on interop types; check each target is in the unstripped list before relying on it):
- Host, postfix: `ChickenLegsBehaviour.StandUp/SitDown(float)/StandUpImmediately/SitDownImmediately/TeleportTo/
  TeleportToPositionAndSnapOntoTerrain` → HutEvent. Poll `anim`/transform in CoopDriver tick for HutState (no hook).
- Host, postfix `ChickenLegsBehaviour.DetermineMoveParameter` → when a client holds steering, overwrite `moveAxis` (phase 2).
- Host: on HutRequest.Call → `hut.TryCallTo(pos)` (or `GameEvents.Home.TryCallHut.Invoke(pos)`), reply refusal kind in a HutEvent.
- Client, prefix `return false`: `ChickenLegsAIController.Update/LateUpdate/OnAnimatorMove/ApplyMovement`,
  `ChickenLegsBehaviour.TickMovement/DetermineMoveParameter/Rotate/MoveTowardsTarget/EvaluateStandingState/TickLookAtTakeover/
  TickCaptureHold/StartControlling/TurnHouseAroundAndPutItBackOntoTheMap`. Keep `LateUpdate`/`FootIK` running.
  Also `anim.applyRootMotion = false` on client.
- Client, prefix `ChickenLegsBehaviour.TryCallTo` → send HutRequest.Call, return `CallResult.Coming` (skip original).
- Client: apply HutState in `LateUpdate` *before* PlayerPlatformCarry (or in Update) so the local rider is carried by the
  mirror's collider delta; on HutEvent call the matching ChickenLegsBehaviour method with a reentrancy guard (so the
  sit flatten/prop hide runs locally) and then keep overwriting pose.
- Both: puppets of riders read the frame byte; never touch `PlayerHouseInfo`, `PlayerHutBoardDetector.PlayerAboard`.

**Client's own hut**: there is exactly one logical hut — the client's local instance becomes the host's hut. Client save:
don't let it persist host pose blindly (out of scope; co-op saves are host-side). If the client's save differs from the host
(different house), the mirror shows the wrong building → requires the save-sync slice; for now require same save.

## 4. Risks / open questions (runtime tests)
1. **Test saves have no hut** (ritual not done, parked at 0,4.4,0). Need a post-ritual save copied into both CoopSaves.
2. Real hierarchy: log paths of ChickenLegsBehaviour, AIController, `anim`, `platformFollower`, `chickenHouseRoot`,
   `PlayerHutBoardDetector.HutPlatform`, `HutHouseBounds.PlatformRoot`; which GO does root motion move?
3. Is `ChildPlayerWhenStandingOnHouse` present/enabled? If it reparents the player, rider math changes (localPosition already).
4. Does PlayerPlatformCarry carry a player on a kinematically-teleported collider? Jumps > `TELEPORT_DISTANCE` drop the carry;
   interpolation must be smooth and ordered before the carry's LateUpdate (script execution order unknown).
5. Collider movement without Rigidbody: Physics sync (`Physics.SyncTransforms`/autoSync) may be needed after writing pose.
6. Are animation events (OnSitDownComplete, foot touch) driving the sit environment flow? If stripped Update blocks them,
   flatten/prop hide may never fire on the client; or firing twice (event + our replay) may double-apply.
7. Which listed methods are stripped (957 failed unstripping) — Harmony on a stripped method silently does nothing.
8. Latency: rider standing still on a moving hut on the host appears to slide if host hut and client mirror differ by
   ~100 ms; platform-local send hides it for riders, not for terrain contact. Measure.
9. LookAtTakeover/stuck UI/`HutCameraDirector` stand-up cutscene on the client mirror: may fight our pose writes.
10. `BeyondEdgeOfMapHouseTravelTrigger` (map travel), backport `ActualPort` to the hut, tutorial `ParkLegsOffMap` — all
    must be host-gated later.
11. Steering by a client: is the steering seat (`HutControlPlayerSittingPlace`) one-seat? Who gets the camera?

## 5. First implementation step
Read-only probe, no sync: in CoopDriver (both roles) after scene load, `FindFirstObjectByType<ChickenLegsBehaviour>()` and
log once: hierarchy paths (Q2), `anim.applyRootMotion`, ControlMode, IsStanding, ChildPlayerWhenStandingOnHouse count/enabled,
`PlayerHutBoardDetector.HutPlatform` path; then every 2 s log root pos/rot, platformFollower pos, `PlayerHutBoardDetector.PlayerAboard`,
and the local body in platformFollower-local coords. Run on a post-ritual save, ride and call the hut on the host. That
answers Q1–Q4 and fixes which transform HutState must carry before writing messages 18–20.
