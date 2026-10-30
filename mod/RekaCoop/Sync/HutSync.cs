using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// There is one hut and the host owns it. Guests keep the hut from their copy of the save but turn it into a mirror:
// its AI, steering and root motion are switched off, and the host's pose and animator state are played back on it.
// A guest calling the hut asks the host to do it. Only the host drives: a guest taking the controls (or a guest's talk
// with the hut ending in "go where I say", which runs on the host) is refused. While the host drives, everyone else
// sees the host's camera, the way they see a conversation's, and can't move. (Guest steering below is dev only now.)
public static class HutSync
{
    private const float SendHz = 20f;
    private const float InterpDelay = 0.1f;

    private class Sample
    {
        public Vector3 Pos;
        public Quaternion Rot;
        public bool Standing;
        public byte SitEnv;  // the host's last sit/stand ground effect: 0 none yet, 1 sat (ground flattened, props hidden), 2 stood
        public Vector3 FlattenPos;  // where the host's hut made its clearing when it last sat
        public bool Driving;  // the host is at the controls
        public float[] Floats;
        public ulong Bools;
        public int[] StateHash;
        public float[] LayerWeight;
        public float[] NormTime;  // how far into its state each layer is
    }

    private static ChickenLegsBehaviour _hut;
    private static int[] _floatParams, _boolParams;
    private static float _nextSend;
    private static readonly List<(float time, Sample s)> Buffer = new();
    private static float[] _mismatchSince = new float[0];
    private static byte _sitEnv, _appliedSitEnv;

    // Guest: the host is sending the hut, so the local one must not move by itself.
    public static bool Mirroring { get; private set; }

    // The moving floor riders stand on; it bobs with the gait, so riders are placed relative to it.
    public static Transform Floor => Hut?.platformFollower;

    public static ChickenLegsBehaviour Hut => _hut != null ? _hut : _hut = RekaCoop.Sync.Find.One<ChickenLegsBehaviour>();

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) =>
        {
            if (type == MsgType.HutState) OnState(r);
            else if (type == MsgType.HutRequest && session.Role == Role.Host) OnRequest(from, r);
            else if (type == MsgType.HutRequest && (Request)r.GetByte() == Request.Refused)
            {
                Plugin.Logger.LogInfo("Hut: the host refused the controls");
                DialogueSync.ShowAfterTalk(OnlyHost);
            }
        };
        session.PlayerLeft += id => { if (id == _steerBy) StopSteer("they left"); };
    }

    public static void Tick(CoopSession session)
    {
        var hut = Hut;
        if (hut == null || hut.anim == null) { Mirroring = false; Buffer.Clear(); _sitEnv = _appliedSitEnv = 0; WatchDrive(false); return; }
        CacheParams(hut.anim);

        if (session.Role == Role.Host)
        {
            if (_steerBy >= 0 && Time.unscaledTime - _steerHeard > SteerTimeout) StopSteer("no input");
            if (!session.IsConnected || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / SendHz;
            var w = session.BeginMessage(MsgType.HutState);
            Write(w, Capture(hut));
            session.Send(w, DeliveryMethod.Sequenced);
        }
        else if (Buffer.Count > 0)
        {
            Mirroring = true;
            hut.anim.applyRootMotion = false;
            Play(hut);
            SendSteer(hut);
            WatchDrive(Buffer[^1].s.Driving && Time.unscaledTime - Buffer[^1].time < 1f);
        }
        else WatchDrive(false);
    }

    internal const string OnlyHost = "Only the host can drive the hut.";

    // Host: at the controls itself (not for a guest).
    internal static bool HostDriving => CoopDriver.Session?.Role == Role.Host && _steerBy < 0 && Hut != null && Hut.isBeingControlled;

    // Guest: the host is driving; the host's camera shows here and this player stays put.
    internal static bool WatchingDrive { get; private set; }

    private static void WatchDrive(bool on)
    {
        if (on == WatchingDrive) return;
        WatchingDrive = on;
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        try
        {
            if (on) { DialogueSync.CloseStrayLine(); me?.BlockMovement(true); }
            else me?.UnblockMovement();
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Hut: movement block: {e.Message}"); }
        Plugin.Logger.LogInfo(on ? "Hut: the host is driving, watching" : "Hut: the host stopped driving");
    }

    // A guest-led talk ran on the host: the guest who led it is told no.
    internal static void Refuse(int playerId)
    {
        var session = CoopDriver.Session;
        if (session == null) return;
        var w = session.BeginMessage(MsgType.HutRequest);
        w.Put((byte)Request.Refused);
        session.SendTo(playerId, w, DeliveryMethod.ReliableOrdered);
    }

    // Guest at the controls.
    internal static bool GuestSteering;
    private static float _nextSteer;

    private static void SendSteer(ChickenLegsBehaviour hut)
    {
        bool steering = hut.isBeingControlled;
        if (steering != GuestSteering)
        {
            GuestSteering = steering;
            SendRequest(steering ? Request.SteerStart : Request.SteerStop);
            Plugin.Logger.LogInfo($"Hut: {(steering ? "took" : "left")} the controls");
        }
        if (!steering || Time.unscaledTime < _nextSteer) return;
        _nextSteer = Time.unscaledTime + 1f / SendHz;
        var axis = hut.moveAxis;
        SendRequest(Request.Steer, new Vector3(axis.x, axis.y, hut.toggleSprintToggledOn ? 1 : 0), DeliveryMethod.Sequenced);
    }

    // Host: a guest is steering. The hut is put in player control the way the game does it, minus the camera.
    private const float SteerTimeout = 1f;
    private static int _steerBy = -1;
    private static Vector2 _steerAxis;
    private static bool _steerSprint;
    private static float _steerHeard;

    internal static bool SteeredByGuest(ChickenLegsBehaviour hut, out Vector2 axis, out bool sprint)
    {
        axis = _steerAxis; sprint = _steerSprint;
        return _steerBy >= 0 && hut != null && hut.Pointer == Hut?.Pointer;
    }

    internal static ChickenLegsBehaviour.ControlMode SteerMode = ChickenLegsBehaviour.ControlMode.PlayerControl;  // dev: steermode

    private static void StartSteer(int playerId)
    {
        var hut = Hut;
        if (hut == null) return;
        if (hut.isBeingControlled && _steerBy < 0) { Plugin.Logger.LogInfo($"Hut: player {playerId} can't steer, the host is at the controls"); return; }
        _steerBy = playerId;
        _steerHeard = Time.unscaledTime;
        _steerAxis = Vector2.zero;
        hut.isBeingControlled = true;
        // Player control switches the floor's colliders off (the driver sits in the seat); anyone standing aboard
        // would drop through, so the ones that were on stay on while a guest drives.
        KeepOn.Clear();
        if (hut.platformFollower != null)
            foreach (var c in hut.platformFollower.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger) KeepOn.Add(c);
        hut.SetControlMode(SteerMode);
        KeepFloor();
        Plugin.Logger.LogInfo($"Hut: player {playerId} took the controls");
    }

    private static readonly List<Collider> KeepOn = new();

    internal static void KeepFloor()
    {
        foreach (var c in KeepOn)
            if (c != null && !c.enabled) c.enabled = true;
    }

    private static void StopSteer(string why)
    {
        if (_steerBy < 0) return;
        var hut = Hut;
        Plugin.Logger.LogInfo($"Hut: player {_steerBy} left the controls ({why})");
        _steerBy = -1;
        _steerAxis = Vector2.zero;
        if (hut == null) return;
        hut.isBeingControlled = false;
        hut.moveAxis = Vector2.zero;
        hut.oldMoveAxisRaw = Vector2.zero;
        // Like letting go of the controls in the game: it stays where it was steered to (AI control would walk it back).
        hut.SetControlMode(ChickenLegsBehaviour.ControlMode.None);
    }

    private static Sample Capture(ChickenLegsBehaviour hut)
    {
        var anim = hut.anim;
        var s = new Sample
        {
            Pos = hut.transform.position, Rot = hut.transform.rotation, Standing = hut.isStanding, SitEnv = _sitEnv, Driving = HostDriving,
            FlattenPos = hut._hasSitDownFlattenPos ? hut._sitDownFlattenPos : hut.transform.position,
            Floats = new float[_floatParams.Length], StateHash = new int[anim.layerCount], LayerWeight = new float[anim.layerCount], NormTime = new float[anim.layerCount],
        };
        for (int i = 0; i < _floatParams.Length; i++) s.Floats[i] = anim.GetFloat(_floatParams[i]);
        for (int i = 0; i < _boolParams.Length; i++) if (anim.GetBool(_boolParams[i])) s.Bools |= 1UL << i;
        for (int l = 0; l < anim.layerCount; l++)
        {
            var info = anim.GetCurrentAnimatorStateInfo(l);
            s.StateHash[l] = info.fullPathHash;
            s.NormTime[l] = info.normalizedTime;
            s.LayerWeight[l] = anim.GetLayerWeight(l);
        }
        return s;
    }

    private static void Write(NetDataWriter w, Sample s)
    {
        w.Put(s.Pos.x); w.Put(s.Pos.y); w.Put(s.Pos.z);
        w.Put(s.Rot.x); w.Put(s.Rot.y); w.Put(s.Rot.z); w.Put(s.Rot.w);
        w.Put(s.Standing);
        w.Put(s.SitEnv);
        w.Put(s.FlattenPos.x); w.Put(s.FlattenPos.y); w.Put(s.FlattenPos.z);
        w.Put(s.Driving);
        w.Put((byte)s.Floats.Length);
        foreach (var f in s.Floats) w.Put(f);
        w.Put(s.Bools);
        w.Put((byte)s.StateHash.Length);
        for (int l = 0; l < s.StateHash.Length; l++) { w.Put(s.StateHash[l]); w.Put(s.LayerWeight[l]); w.Put(s.NormTime[l]); }
    }

    private static void OnState(NetDataReader r)
    {
        var s = new Sample
        {
            Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
            Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
            Standing = r.GetBool(),
            SitEnv = r.GetByte(),
            FlattenPos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
            Driving = r.GetBool(),
        };
        s.Floats = new float[r.GetByte()];
        for (int i = 0; i < s.Floats.Length; i++) s.Floats[i] = r.GetFloat();
        s.Bools = r.GetULong();
        int layers = r.GetByte();
        s.StateHash = new int[layers];
        s.LayerWeight = new float[layers];
        s.NormTime = new float[layers];
        for (int l = 0; l < layers; l++) { s.StateHash[l] = r.GetInt(); s.LayerWeight[l] = r.GetFloat(); s.NormTime[l] = r.GetFloat(); }
        Buffer.Add((Time.unscaledTime, s));
        if (Buffer.Count > 40) Buffer.RemoveAt(0);
    }

    internal static void NoteSitEnv(bool sitDown) => _sitEnv = sitDown ? (byte)1 : (byte)2;

    private static void Play(ChickenLegsBehaviour hut)
    {
        float renderTime = Time.unscaledTime - InterpDelay;
        int i = Buffer.Count - 1;
        while (i > 0 && Buffer[i - 1].time > renderTime) i--;
        var b = Buffer[i];
        var a = i > 0 ? Buffer[i - 1] : b;
        float t = b.time > a.time ? Mathf.Clamp01((renderTime - a.time) / (b.time - a.time)) : 1f;
        hut.transform.SetPositionAndRotation(Vector3.Lerp(a.s.Pos, b.s.Pos, t), Quaternion.Slerp(a.s.Rot, b.s.Rot, t));
        hut.isStanding = b.s.Standing;
        // The guest's hut never sits or stands up by itself, so its ground effects are run when the host's ran.
        if (b.s.SitEnv != 0 && b.s.SitEnv != _appliedSitEnv)
        {
            _appliedSitEnv = b.s.SitEnv;
            try
            {
                // Where it sits: the clearing (ground flattened, trees pushed aside) is made around this spot, which the
                // host's own sitting sets; left alone, the mirror cleared around wherever it last sat.
                if (b.s.SitEnv == 1) hut.SetSitDownFlattenPos(b.s.FlattenPos);
                hut.EnvironmentSittingChanges(b.s.SitEnv == 1);
                // The host's own update loop then applies it (flattening the ground); that loop is off on a mirror.
                if (b.s.SitEnv == 1) hut.ApplySitEnvironmentChanges(hut.BeginSitEnvironmentChange(), "coop mirror");
                Plugin.Logger.LogInfo($"Hut: ran the host's {(b.s.SitEnv == 1 ? "sitting" : "standing")} ground changes at {b.s.FlattenPos}");
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"Hut: ground effects: {e.Message}"); }
        }

        var anim = hut.anim;
        var s = b.s;
        for (int k = 0; k < _floatParams.Length && k < s.Floats.Length; k++) anim.SetFloat(_floatParams[k], s.Floats[k]);
        for (int k = 0; k < _boolParams.Length; k++) anim.SetBool(_boolParams[k], (s.Bools & (1UL << k)) != 0);
        if (_mismatchSince.Length != s.StateHash.Length) _mismatchSince = new float[s.StateHash.Length];
        for (int l = 0; l < s.StateHash.Length && l < anim.layerCount; l++)
        {
            if (l > 0) anim.SetLayerWeight(l, s.LayerWeight[l]);
            if (anim.GetCurrentAnimatorStateInfo(l).fullPathHash == s.StateHash[l] || anim.IsInTransition(l)) { _mismatchSince[l] = -1; continue; }
            if (_mismatchSince[l] < 0) _mismatchSince[l] = Time.unscaledTime;
            if (Time.unscaledTime - _mismatchSince[l] < 0.2f) continue;  // let parameters drive transitions first
            // Into the host's state where the host is in it: from its start, a state the guest's own parameters kept
            // leaving was restarted every few tenths of a second (the walking legs looped a short stretch).
            anim.CrossFade(s.StateHash[l], 0.15f, l, Mathf.Repeat(s.NormTime[l] + InterpDelay / Mathf.Max(0.1f, anim.GetCurrentAnimatorStateInfo(l).length), 1f));
            _mismatchSince[l] = -1;
        }
    }

    private static void CacheParams(Animator anim)
    {
        if (_floatParams != null) return;
        var floats = new List<int>();
        var bools = new List<int>();
        foreach (var p in anim.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Float) floats.Add(p.nameHash);
            else if (p.type == AnimatorControllerParameterType.Bool && bools.Count < 64) bools.Add(p.nameHash);
        }
        _floatParams = floats.ToArray();
        _boolParams = bools.ToArray();
        Plugin.Logger.LogInfo($"HutSync: {_floatParams.Length} float and {_boolParams.Length} bool params, {anim.layerCount} layers");
    }

    // Guest -> host requests.
    public enum Request : byte { Call = 1, Stand = 2, Sit = 3, SteerStart = 4, Steer = 5, SteerStop = 6, Refused = 7 }  // Refused: host -> guest

    public static void SendRequest(Request kind, Vector3 pos = default, DeliveryMethod how = DeliveryMethod.ReliableOrdered)
    {
        var session = CoopDriver.Session;
        if (session == null) return;
        var w = session.BeginMessage(MsgType.HutRequest);
        w.Put((byte)kind);
        w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
        session.Send(w, how);
    }

    private static void OnRequest(RemotePlayer from, NetDataReader r)
    {
        var kind = (Request)r.GetByte();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        var hut = Hut;
        if (hut == null) return;
        switch (kind)
        {
            case Request.Call:
                Plugin.Logger.LogInfo($"Hut: player {from.PlayerId} calls the hut to {pos}: {hut.TryCallTo(pos)}");
                break;
            case Request.Stand: hut.StandUp(); break;
            case Request.Sit: hut.SitDown(); break;
            case Request.SteerStart: StartSteer(from.PlayerId); break;
            case Request.SteerStop: if (from.PlayerId == _steerBy) StopSteer("they let go"); break;
            case Request.Steer:
                if (from.PlayerId != _steerBy) { StartSteer(from.PlayerId); if (from.PlayerId != _steerBy) break; }  // missed the start
                _steerAxis = new Vector2(Mathf.Clamp(pos.x, -1, 1), Mathf.Clamp(pos.y, -1, 1));
                _steerSprint = pos.z > 0.5f;
                _steerHeard = Time.unscaledTime;
                break;
        }
    }

    public static string Describe()
    {
        var hut = Hut;
        if (hut == null) return "no hut";
        string P(Transform t) => t == null ? "null" : GearObjects.PathOf(t);
        var board = RekaCoop.Sync.Find.One<PlayerHutBoardDetector>();
        return $"hut {P(hut.transform)} pos={hut.transform.position} standing={hut.isStanding} moving={hut.HutIsMoving} mode={hut.CurrentControlMode} " +
               $"speed={hut.currentSpeed:0.00} anim={P(hut.anim?.transform)} rootMotion={hut.anim?.applyRootMotion} platform={P(hut.platformFollower)} " +
               $"houseRoot={P(hut.chickenHouseRoot)} ai={P(hut.aiController?.transform)} boardPlatform={P(board?.HutPlatform)} aboard={PlayerHutBoardDetector.PlayerAboard} mirroring={Mirroring} buffered={Buffer.Count} steerBy={_steerBy} axis={_steerAxis} guestSteering={GuestSteering} hostDriving={HostDriving} watchingDrive={WatchingDrive}";
    }
}

// The guest's hut is a mirror of the host's: nothing local may move it.
[HarmonyPatch]
internal static class GuestHutMirror
{
    private static bool Run() => !HutSync.Mirroring;
    internal static readonly HashSet<string> DevAllow = new();  // dev: let these run on the mirror anyway
    private static bool Run(string name) => !HutSync.Mirroring || DevAllow.Contains(name);

    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsAIController), nameof(ChickenLegsAIController.Update))] private static bool AIUpdate() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsAIController), nameof(ChickenLegsAIController.LateUpdate))] private static bool AILateUpdate() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsAIController), nameof(ChickenLegsAIController.OnAnimatorMove))] private static bool AIAnimatorMove() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsAIController), nameof(ChickenLegsAIController.ApplyMovement))] private static bool AIApplyMovement() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.TickMovement))] private static bool TickMovement() => Run("TickMovement");
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.DetermineMoveParameter))]
    private static bool DetermineMoveParameter(ChickenLegsBehaviour __instance) => Run("DetermineMoveParameter") || __instance.isBeingControlled;  // reads the guest's own input

    // Host: a guest's input replaces the (idle) local one.
    [HarmonyPostfix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.DetermineMoveParameter))]
    private static void SteerByGuest(ChickenLegsBehaviour __instance)
    {
        if (!HutSync.SteeredByGuest(__instance, out var axis, out var sprint)) return;
        HutSync.KeepFloor();
        __instance.moveAxis = axis;
        __instance.oldMoveAxisRaw = axis;
        __instance.toggleSprintToggledOn = sprint;
    }
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.Rotate))] private static bool Rotate() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.MoveTowardsTarget))] private static bool MoveTowardsTarget() => Run();
    [HarmonyPostfix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.EnvironmentSittingChanges))]
    private static void SittingChanges(bool sitDown) { if (!HutSync.Mirroring) HutSync.NoteSitEnv(sitDown); }

    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.EvaluateStandingState))] private static bool EvaluateStandingState() => Run();
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.TickLookAtTakeover))] private static bool TickLookAtTakeover() => Run("TickLookAtTakeover");
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.TickCaptureHold))] private static bool TickCaptureHold() => Run();

    [HarmonyPostfix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.OnLookAtPromptPressed))]
    private static void LookAtPressed() => Plugin.Logger.LogInfo($"Hut: look-at prompt pressed (mirroring={HutSync.Mirroring})");

    // Only the host takes the controls. On a guest's copy they're refused outright; on the host, when the talk that
    // asked for it was led by a guest (it runs here), that guest is told.
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.StartControlling))]
    private static bool StartControlling()
    {
        var session = CoopDriver.Session;
        if (session == null || !session.IsConnected) return true;
        if (HutSync.Mirroring) { DialogueSync.ShowLocal(HutSync.OnlyHost); Plugin.Logger.LogInfo("Hut: refused the controls (guest)"); return false; }
        int leader = DialogueSync.RecentLeader;
        if (session.Role != Role.Host || leader < 0 || leader == session.LocalPlayerId) return true;
        HutSync.Refuse(leader);
        Plugin.Logger.LogInfo($"Hut: refused the controls to {NameTags.NameOf(leader)}'s talk");
        return false;
    }

    // A guest calling the hut (backport item) asks the host instead.
    [HarmonyPrefix, HarmonyPatch(typeof(ChickenLegsBehaviour), nameof(ChickenLegsBehaviour.TryCallTo))]
    private static bool TryCallTo(Vector3 pos, ref ChickenLegsBehaviour.CallResult __result)
    {
        if (!HutSync.Mirroring) return true;
        HutSync.SendRequest(HutSync.Request.Call, pos);
        __result = ChickenLegsBehaviour.CallResult.Coming;
        return false;
    }
}
