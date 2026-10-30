using System.Linq;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Streams the local player's body at SendHz and shows every other player as a Puppet.
// The host relays each client's state to the other clients.
public static class PlayerSync
{
    private const float SendHz = 15f;

    // Animator parameter hashes by type, in controller order. Same controller on every machine.
    public class AnimParams { public int[] Floats; public int[] Bools; }
    public static AnimParams Params { get; private set; }

    private static readonly Dictionary<int, Puppet> Puppets = new();
    private static PlayerMovement_2021 _local;
    private static float _nextSend;

    public static int PuppetCount => Puppets.Count;
    public static bool InWorld => _local != null;
    public static Transform LocalBody => _local != null ? _local.transform : null;
    public static Vector3? LocalFeet => _local != null ? _local.transform.position : null;
    public static Transform PuppetRoot(int playerId) => Puppets.TryGetValue(playerId, out var p) && p.Root != null ? p.Root.transform : null;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.PlayerState) OnState(session, from, r); };
        session.PlayerLeft += RemovePuppet;
    }

    public static void Tick(CoopSession session)
    {
        if (_local == null)
        {
            if (!_resetWhileAway)
            {
                ClearPuppets();  // left the game scene, or not in it yet
                _assembledSince = -1;
                Appearance.AppearanceSync.ResetLocal();
                GearSync.ResetLocal();
                ResetWorld();
                _resetWhileAway = true;
            }
            // Twice a second: a search that finds nothing walks the whole scene (and loading scenes are big).
            if (Time.unscaledTime < _nextLocalLook) return;
            _nextLocalLook = Time.unscaledTime + 0.5f;
            _local = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            if (_local == null) return;
            _resetWhileAway = false;
            CacheParams(_local.playerAnimator);
        }

        if (session.IsConnected && Time.unscaledTime >= _nextSend)
        {
            _nextSend = Time.unscaledTime + 1f / SendHz;
            var snap = Capture(_local);
            var w = session.BeginMessage(MsgType.PlayerState);
            w.Put(session.LocalPlayerId);
            snap.Write(w);
            session.Send(w, DeliveryMethod.Sequenced);
        }

        NameTags.Tick(session);
        long heap = Il2CppSystem.GC.GetTotalMemory(false);
        if (heap > _lastHeap) HeapGrowth += heap - _lastHeap;
        _lastHeap = heap;

        if (BodyAssembled() && !PausedTicks)
        {
            Timed("scan", RekaCoop.Sync.Find.Tick);
            Timed("appearance", () => Appearance.AppearanceSync.Tick(session));
            Timed("gear", () => GearSync.Tick(session, _local));
            Timed("inventory", () => InventorySync.Tick(session));
            Timed("world", () => WorldObjects.Tick(session));
            Timed("build", () => BuildSync.Tick(session));
            Timed("blueprints", () => BlueprintSync.Tick(session));
            Timed("story", () => StorySync.Tick(session));
            Timed("containers", () => ContainerSync.Tick(session));
            Timed("trials", () => TrialSync.Tick(session));
            Timed("tutorial", () => TutorialSync.Tick(session));
            Timed("map", () => MapSync.Tick(session));
            Timed("saveables", () => SaveableSync.Tick(session));
            Timed("active", () => ActiveSync.Tick(session));
            Timed("digest", () => DigestSync.Tick(session));
            Timed("stations", () => StationSync.Tick(session));
            Timed("places", () => PoiSync.Tick(session));
            Timed("ai", () => AiSync.Tick(session));
            ReportTimes();
        }

        foreach (var p in Puppets.Values) p.Update();

        if (Time.unscaledTime >= _nextDiag && Puppets.Count > 0)
        {
            _nextDiag = Time.unscaledTime + 5f;
            foreach (var p in Puppets.Values)
                Plugin.Logger.LogInfo($"[diag] local at {_local.transform.position}; {p.Describe(_local.transform.position)}");
        }
    }

    private static float _nextDiag;
    internal static long HeapGrowth;   // game heap growth summed over frames, leaving out collections: its allocations
    private static long _lastHeap;
    internal static bool PausedTicks;
    private static bool _resetWhileAway;
    private static float _nextLocalLook;
    internal static string SkippedTick;  // dev: one tick left out, to measure what it costs  // dev: alloc measurements

    // A different world under the same body (travel, or loading a save while playing): everything tied to the old
    // map starts over, and a guest asks for the shared pack again (the loaded save may hold an older one).
    public static void ResetWorld()
    {
        Find.Forget();
        InventorySync.ResetLocal();
        WorldObjects.ResetLocal();
        BuildSync.ResetLocal();
        BlueprintSync.ResetLocal();
        StorySync.ResetLocal();
        ContainerSync.ResetLocal();
        TrialSync.ResetLocal();
        ConditionSync.ResetLocal();
        TutorialSync.ResetLocal();
        MapSync.ResetLocal();
        StoryInteract.ResetLocal();
        SaveableSync.ResetLocal();
        ActiveSync.ResetLocal();
        DigestSync.ResetLocal();
        StationSync.ResetLocal();
        PoiSync.ResetLocal();
        AiSync.ResetLocal();
        CarrySync.ResetLocal();
        MirrorSync.ResetLocal();
        BirdSync.ResetLocal();
    }

    private static void OnState(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        int playerId = r.GetInt();
        // A client may only speak for itself; the host knows who each peer is.
        if (session.Role == Role.Host) playerId = from.PlayerId;
        var snap = PlayerSnapshot.Read(r);

        if (session.Role == Role.Host)
        {
            var w = session.BeginMessage(MsgType.PlayerState);
            w.Put(playerId);
            snap.Write(w);
            session.Send(w, DeliveryMethod.Sequenced, exceptPlayerId: playerId);
        }

        HeardAt[playerId] = Time.unscaledTime;
        if (_local == null || playerId == session.LocalPlayerId) return;
        if (!Puppets.TryGetValue(playerId, out var puppet))
        {
            if (!BodyAssembled()) return;
            puppet = new Puppet(playerId, _local.gameObject);
            Puppets[playerId] = puppet;
            var look = Appearance.AppearanceSync.LookOf(playerId);
            if (look != null) puppet.ApplyLook(look);
            var gear = GearSync.StateOf(playerId);
            if (gear != null) puppet.Gear.Apply(gear, _local);
            Plugin.Logger.LogInfo($"Spawned puppet for player {playerId}");
        }
        puppet.Push(snap);
    }

    // CharacterAssembler adds the body, head and clothes meshes a few seconds after the player spawns.
    // The puppet is a copy of the local body, so wait until those meshes have existed for a moment.
    private static float _assembledSince = -1;

    private static bool BodyAssembled()
    {
        if (_local.GetComponentsInChildren<SkinnedMeshRenderer>(false).Length == 0)
        {
            _assembledSince = -1;
            return false;
        }
        if (_assembledSince < 0) _assembledSince = Time.unscaledTime;
        return Time.unscaledTime - _assembledSince > 2f;
    }

    private static PlayerSnapshot Capture(PlayerMovement_2021 local)
    {
        var t = local.transform;
        var anim = local.playerAnimator;
        var s = new PlayerSnapshot { Position = t.position, Yaw = t.eulerAngles.y, Floats = new float[Params.Floats.Length] };
        var floor = HutSync.Floor;
        if (PlayerHutBoardDetector.PlayerAboard && floor != null)
        {
            s.OnHut = true;
            s.Position = floor.InverseTransformPoint(t.position);
            s.Yaw = Mathf.DeltaAngle(floor.eulerAngles.y, t.eulerAngles.y);
        }
        for (int i = 0; i < Params.Floats.Length; i++) s.Floats[i] = anim.GetFloat(Params.Floats[i]);
        for (int i = 0; i < Params.Bools.Length; i++)
            if (anim.GetBool(Params.Bools[i])) s.Bools |= 1UL << i;
        for (int layer = 0; layer < PlayerSnapshot.Layers && layer < anim.layerCount; layer++)
        {
            var info = anim.GetCurrentAnimatorStateInfo(layer);
            s.StateHash[layer] = info.fullPathHash;
            s.StateTime[layer] = info.normalizedTime;
            s.LayerWeight[layer] = anim.GetLayerWeight(layer);
        }
        if (ArmBones.RigsActive(local.transform))
        {
            var bones = ArmBones.Of(anim.transform);
            if (bones != null)
            {
                s.Arms = new Quaternion[bones.Length];
                for (int i = 0; i < bones.Length; i++) s.Arms[i] = bones[i].localRotation;
            }
        }
        return s;
    }

    private static void CacheParams(Animator anim)
    {
        if (Params != null || anim == null) return;
        var floats = new List<int>();
        var bools = new List<int>();
        foreach (var p in anim.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Float) floats.Add(p.nameHash);
            else if (p.type == AnimatorControllerParameterType.Bool && bools.Count < 64) bools.Add(p.nameHash);
        }
        Params = new AnimParams { Floats = floats.ToArray(), Bools = bools.ToArray() };
        Plugin.Logger.LogInfo($"PlayerSync: {Params.Floats.Length} float and {Params.Bools.Length} bool animator params");
    }

    public static string DescribeGear()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in Puppets.Values) sb.Append($"puppet {p.PlayerId}:{p.Gear.Describe()}\n");
        return sb.ToString();
    }

    // Slowest frame per module over the last 30 s, so a costly poll shows up in the log.
    private static readonly Dictionary<string, double> Worst = new();
    private static readonly System.Diagnostics.Stopwatch Clock = new();
    private static float _nextTimes = 30;

    private static void Timed(string what, System.Action tick)
    {
        if (what == SkippedTick) return;
        Clock.Restart();
        tick();
        double ms = Clock.Elapsed.TotalMilliseconds;
        if (ms > Worst.GetValueOrDefault(what)) Worst[what] = ms;
    }

    // The whole frame too (game and mod), to tell the mod's hitches from the game's.
    private static int _frames, _hitches;
    private static double _frameSum, _frameWorst;

    private static int _prevGameGcs, _lastNetGcs, _netGcHitches, _lastGameGcs, _gameGcsAt, _netGcsAt, _gcHitches;
    private static long _managedAt;

    private static void ReportTimes()
    {
        double dt = Time.unscaledDeltaTime * 1000;
        _frames++; _frameSum += dt; _frameWorst = System.Math.Max(_frameWorst, dt);
        // Hitch frames during which (or a frame before) the game's own (IL2CPP, Boehm) collector started, to tell GC pauses from the rest.
        int gameGcs = Il2CppSystem.GC.CollectionCount(0);
        int netGcs = System.GC.CollectionCount(0);
        if (dt > 50) { _hitches++; if (gameGcs != _prevGameGcs) _gcHitches++; if (netGcs != _lastNetGcs) _netGcHitches++; }
        _prevGameGcs = _lastGameGcs; _lastGameGcs = gameGcs; _lastNetGcs = netGcs;
        if (Time.unscaledTime < _nextTimes) return;
        _nextTimes = Time.unscaledTime + 30;
        long managed = System.GC.GetTotalAllocatedBytes();
        long gameHeap = Il2CppSystem.GC.GetTotalMemory(false);
        Plugin.Logger.LogInfo($"[perf] game GCs {gameGcs - _gameGcsAt} in 30 s ({_gcHitches} in hitch frames), game heap {gameHeap >> 20} MB; mod's .NET allocations {(managed - _managedAt) / 30.0 / 1024:0} KB/s, .NET GCs {System.GC.CollectionCount(0) - _netGcsAt} ({_netGcHitches} in hitch frames, gen2 {System.GC.CollectionCount(2)})");
        _gameGcsAt = gameGcs; _managedAt = managed; _netGcsAt = System.GC.CollectionCount(0); _gcHitches = _netGcHitches = 0;
        var slow = Worst.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.0}");
        Plugin.Logger.LogInfo($"[perf] frames avg {_frameSum / System.Math.Max(1, _frames):0.0} worst {_frameWorst:0} ms, {_hitches} over 50 ms; slowest mod ticks (ms) in 30 s: {string.Join(", ", slow)}; scene searches that found nothing: {string.Join(", ", Find.Misses.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Find.Misses.Clear();
        Worst.Clear();
        _frames = _hitches = 0; _frameSum = _frameWorst = 0;
    }

    // After the puppets' animators ran this frame.
    public static void LateTick()
    {
        foreach (var p in Puppets.Values) p.LateUpdate();
    }

    // Within range of the local player or any puppet.
    public static bool NearAnyPlayer(Vector3 at, float range)
    {
        float r2 = range * range;
        if (_local != null && (_local.transform.position - at).sqrMagnitude < r2) return true;
        foreach (var p in Puppets.Values)
            if (p.Root != null && (p.Root.transform.position - at).sqrMagnitude < r2) return true;
        return false;
    }

    public static IEnumerable<(int, Vector3)> PuppetHeads()
    {
        foreach (var p in Puppets.Values) yield return (p.PlayerId, p.Root.transform.position + Vector3.up * 2.1f);
    }

    // A guest sends its body's state only while it is in a world: one still loading (after travel) has gone quiet, and
    // its puppet still stands where it was on the old map.
    private static readonly Dictionary<int, float> HeardAt = new();
    public static bool InWorldNow(int playerId) => HeardAt.TryGetValue(playerId, out var at) && Time.unscaledTime - at < 1.5f;

    public static IEnumerable<(int id, Vector3 pos)> PuppetPositions()
    {
        foreach (var p in Puppets.Values) if (p.Root != null && InWorldNow(p.PlayerId)) yield return (p.PlayerId, p.Root.transform.position);
    }

    public static Vector3? NearestPuppet(Vector3 from) => NearestPuppetTransform(from)?.position;

    public static Transform NearestPuppetTransform(Vector3 from)
    {
        Transform best = null;
        foreach (var p in Puppets.Values)
        {
            var t = p.Root.transform;
            if (best == null || (t.position - from).sqrMagnitude < (best.position - from).sqrMagnitude) best = t;
        }
        return best;
    }

    public static void ApplyLook(int playerId, Appearance.AppearanceData look)
    {
        if (Puppets.TryGetValue(playerId, out var puppet)) puppet.ApplyLook(look);
    }

    public static void ApplyGear(int playerId, GearState gear)
    {
        if (_local != null && Puppets.TryGetValue(playerId, out var puppet)) puppet.Gear.Apply(gear, _local);
    }

    private static void RemovePuppet(int playerId)
    {
        if (!Puppets.Remove(playerId, out var p)) return;
        p.Destroy();
        Plugin.Logger.LogInfo($"Removed puppet for player {playerId}");
    }

    private static void ClearPuppets()
    {
        foreach (var p in Puppets.Values) p.Destroy();
        Puppets.Clear();
    }
}
