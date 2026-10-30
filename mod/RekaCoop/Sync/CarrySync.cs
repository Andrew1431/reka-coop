using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Picking up animals and liftable objects (CarriableObject; the same ones, by POI element id, on every machine).
// The carrier's game does the real carrying; everyone else holds their copy still and keeps it where the carrier's
// body copy has it, from the pose relative to the body that the carrier sends. On drop, everyone puts it down at the
// carrier's spot, and while it rolls or falls from there the dropper sends where it is until it rests, which the others
// follow (their own physics would roll it somewhere else, or a field pumpkin, frozen until picked, would hang in the
// air). Something someone else carries can't be picked up; if two grab it in the same instant, the host's
// order decides and the later one drops it again. AiSync leaves carried animals alone.
public static class CarrySync
{
    private enum Kind : byte { Pick = 1, Drop = 2, Denied = 3, Ready = 4, Roll = 5 }

    private const float ResendEvery = 1f;  // the pose settles over the first moments of a carry

    private class Remote
    {
        public int Player;
        public Vector3 LocalPos;
        public Quaternion LocalRot = Quaternion.identity;
        public CarriableObject Co;
        public bool Held;
        public bool WasKinematic, AiWasEnabled;
        public float[] AnimValues;                     // the carrier's animator for the animal: the carried pose
        public (int hash, float time)[] AnimStates;
    }

    private static readonly Dictionary<string, Remote> Remotes = new();

    // Dropper: things we put down, sent until they rest. Others: things put down elsewhere, following those poses.
    private sealed class Rolling { public CarriableObject Co; public float Until, StillSince = -1, NextSend; }
    private sealed class Follow { public CarriableObject Co; public bool WasKinematic; public Vector3 Pos; public Quaternion Rot; public float Until; }
    private static readonly Dictionary<string, Rolling> Rolls = new();
    private static readonly Dictionary<string, Follow> Follows = new();
    private const float RollFor = 10f, RollSendEvery = 0.1f;
    private static readonly HashSet<IntPtr> CarriedAi = new();
    private static Dictionary<string, CarriableObject> _byKey;
    private static float _indexedAt = -100;
    private static CarriableObject _mine;
    private static string _mineKey;
    private static float _nextResend;
    private static bool _readySent, _quiet;
    private static CoopSession _session;

    public static void Attach(CoopSession session)
    {
        _session = session;
        session.Message += (from, type, r) => { if (type == MsgType.Carry) OnMessage(session, from, r); };
        session.PlayerLeft += id =>
        {
            foreach (var (key, c) in new List<KeyValuePair<string, Remote>>(Remotes))
                if (c.Player == id || (session.Role == Role.Client && id == 0)) Release(key, null, null);
        };
    }

    public static void ResetLocal()
    {
        Remotes.Clear();
        CarriedAi.Clear();
        Rolls.Clear();
        Follows.Clear();
        _byKey = null;
        _mine = null;
        _mineKey = null;
        _readySent = false;
    }

    // AiSync: someone has this animal in their arms.
    internal static bool IsCarried(GeneralAIController ai) => ai != null && CarriedAi.Contains(ai.Pointer);

    private static string ElementId(CarriableObject co)
    {
        var id = co.UniquePoiElementID;
        if (string.IsNullOrEmpty(id) && co.ai != null) id = co.ai._uniquePoiElementID;
        // Story props outside a place (the merchant's wheel) have none; their spot in the scene is the same everywhere.
        // KeyOf caches it before the first pickup moves the object.
        if (string.IsNullOrEmpty(id) && co.ai == null) id = "path:" + ScenePath.Of(co.transform);
        return id;
    }

    // Place id + element id (see ElementKey); call before the game reparents it, as CarryHooks does.
    internal static string KeyOf(CarriableObject co) => ElementKey.Of(co, ElementId(co));
    private static Transform PlaceOf(CarriableObject co) => ElementKey.PlaceOf(co, ElementId(co));

    private static CarriableObject Lookup(string key)
    {
        if (_byKey == null || Time.unscaledTime - _indexedAt > 5f || (_byKey.TryGetValue(key, out var hit) && hit == null) || !_byKey.ContainsKey(key))
        {
            if (_byKey != null && Time.unscaledTime - _indexedAt < 1f) return _byKey.GetValueOrDefault(key);
            _indexedAt = Time.unscaledTime;
            _byKey = new Dictionary<string, CarriableObject>();
            foreach (var co in UnityEngine.Object.FindObjectsByType<CarriableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (KeyOf(co) is string k) _byKey[k] = co;
        }
        return _byKey.GetValueOrDefault(key);
    }

    // What you see in the arms: an animal's own body (its AI), or the object the game's follower moves.
    private static Transform Moving(CarriableObject co) =>
        co.ai != null ? co.ai.transform : co.objectFollower != null ? co.objectFollower.transform : co.transform;

    public static void Tick(CoopSession session)
    {
        if (!PlayerSync.InWorld) return;
        if (session.Role == Role.Client && session.IsConnected && !_readySent)
        {
            _readySent = true;
            var w = session.BeginMessage(MsgType.Carry);
            w.Put((byte)Kind.Ready);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
        // A story step took it from our arms (the merchant's wheel handed in on the host): put down the empty carry,
        // or the player is left holding nothing with the drop prompt up.
        if (_mine != null && !_mine.gameObject.activeInHierarchy)
        {
            Plugin.Logger.LogInfo($"Carry: {_mine.name} was switched off while in our arms; letting go");
            var gone = _mine;
            try { gone.StopCarrying(); } catch (Exception e) { Plugin.Logger.LogWarning($"Carry: letting go failed: {e.Message}"); }
            if (_mine != null) LocalDropped(gone);
        }
        if (_mine != null && session.IsConnected && Time.unscaledTime >= _nextResend)
        {
            if (!_mine.isBeingCarried) LocalDropped(_mine);  // let go some way we didn't hook
            else SendPick(session, session.LocalPlayerId, _mineKey, _mine, toPlayer: -1);
        }

        TickRolls(session);
        foreach (var (key, c) in Remotes)
        {
            if (c.Co == null) c.Co = Lookup(key);
            if (c.Co == null || c.Co.isBeingCarried) continue;  // not here yet, or we hold it ourselves (denied soon)
            var body = c.Player == session.LocalPlayerId ? null : PlayerSync.PuppetRoot(c.Player);
            if (body == null) continue;
            if (!c.Held) Hold(c);
            Moving(c.Co).SetPositionAndRotation(body.TransformPoint(c.LocalPos), body.rotation * c.LocalRot);
            if (c.Co.ai != null) MirrorSync.ApplyAnimator(MirrorSync.AnimatorFor(c.Co.ai), c.AnimValues, c.AnimStates);
        }
    }

    private static void TickRolls(CoopSession session)
    {
        foreach (var (key, roll) in new List<KeyValuePair<string, Rolling>>(Rolls))
        {
            var co = roll.Co;
            if (co == null || !co.gameObject.activeInHierarchy || co.isBeingCarried || !session.IsConnected) { Rolls.Remove(key); continue; }
            var rb = co.rb;
            bool still = rb == null || rb.isKinematic || rb.IsSleeping() || rb.velocity.sqrMagnitude < 0.0025f && rb.angularVelocity.sqrMagnitude < 0.01f;
            if (!still) roll.StillSince = -1;
            else if (roll.StillSince < 0) roll.StillSince = Time.unscaledTime;
            bool rests = still && Time.unscaledTime - roll.StillSince > 0.5f || Time.unscaledTime > roll.Until;
            if (!rests && Time.unscaledTime < roll.NextSend) continue;
            roll.NextSend = Time.unscaledTime + RollSendEvery;
            var t = Moving(co);
            var w = session.BeginMessage(MsgType.Carry);
            w.Put((byte)Kind.Roll);
            w.Put(session.LocalPlayerId);
            w.Put(key);
            Put(w, t.position, t.rotation);
            w.Put(rests);
            session.Send(w, rests ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
            if (rests) { Rolls.Remove(key); Plugin.Logger.LogInfo($"Carry: {co.name} came to rest at {t.position}"); }
        }
        foreach (var (key, f) in new List<KeyValuePair<string, Follow>>(Follows))
        {
            if (f.Co == null) { Follows.Remove(key); continue; }
            var t = Moving(f.Co);
            float k = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, f.Pos, k), Quaternion.Slerp(t.rotation, f.Rot, k));
            if (Time.unscaledTime > f.Until) Settle(key, f.Pos, f.Rot);
        }
    }

    // Others: the dropped thing has come to rest at the dropper's spot; physics as it was before it was picked up.
    private static void Settle(string key, Vector3 pos, Quaternion rot)
    {
        if (!Follows.Remove(key, out var f) || f.Co == null) return;
        Moving(f.Co).SetPositionAndRotation(pos, rot);
        if (f.Co.rb != null) f.Co.rb.isKinematic = f.WasKinematic;
    }

    private static void Hold(Remote c)
    {
        c.Held = true;
        var co = c.Co;
        try
        {
            // The carrier's game finds the animal's AI on pick-up; without it here the animal kept its own pose
            // (standing in mid-air) instead of the carried one.
            if (co.ai == null) co.ResolveCarriedAI();
            MirrorSync.Release(co.ai);
            if (co.ai != null)
            {
                co.HoldCarriedAI();
                CarriedAi.Add(co.ai.Pointer);
                // Its own behaviours would keep setting idle/alert parameters; the carrier's animator is played instead.
                c.AiWasEnabled = co.ai.enabled;
                co.ai.enabled = false;
            }
            if (co.rb != null) { c.WasKinematic = co.rb.isKinematic; co.rb.isKinematic = true; }
            co.SetCarryCollidersEnabled(false);
            co.HideOwnPromptsWhileCarried();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Carry: can't hold {co.name}: {e.Message}"); }
    }

    private static void Release(string key, Vector3? pos, Quaternion? rot)
    {
        if (!Remotes.Remove(key, out var c)) return;
        var co = c.Co ?? Lookup(key);
        if (co == null) return;
        try
        {
            var t = Moving(co);
            if (pos is Vector3 p) t.SetPositionAndRotation(p, rot ?? t.rotation);
            if (!c.Held) return;
            co.SetCarryCollidersEnabled(true);
            co.RestoreOwnPrompts();
            if (co.rb != null) co.rb.isKinematic = c.WasKinematic;
            if (co.ai != null) { CarriedAi.Remove(co.ai.Pointer); co.ai.enabled = c.AiWasEnabled || co.ai.enabled; co.ReleaseCarriedAI(t.position); }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Carry: can't put down {co.name}: {e.Message}"); }
    }

    // Local player picked something up.
    internal static void LocalPicked(CarriableObject co)
    {
        if (_session == null || !co.isBeingCarried) return;
        var key = KeyOf(co);
        if (key == null) { Plugin.Logger.LogInfo($"Carry: {co.name} has no id; not shared"); return; }
        _mine = co;
        _mineKey = key;
        if (co.ai != null) CarriedAi.Add(co.ai.Pointer);
        Plugin.Logger.LogInfo($"Carry: picked up {co.name} ({key})");
        if (_session.IsConnected) SendPick(_session, _session.LocalPlayerId, key, co, toPlayer: -1);
    }

    internal static void LocalDropped(CarriableObject co)
    {
        if (_mine == null || co.Pointer != _mine.Pointer) return;
        var key = _mineKey;
        _mine = null;
        _mineKey = null;
        if (co.ai != null) CarriedAi.Remove(co.ai.Pointer);
        if (_quiet || _session == null || !_session.IsConnected) return;
        var t = Moving(co);
        Plugin.Logger.LogInfo($"Carry: put down {co.name} at {t.position}");
        var w = _session.BeginMessage(MsgType.Carry);
        w.Put((byte)Kind.Drop);
        w.Put(_session.LocalPlayerId);
        w.Put(key);
        Put(w, t.position, t.rotation);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        if (co.ai == null) Rolls[key] = new Rolling { Co = co, Until = Time.unscaledTime + RollFor };  // animals walk off on their own AI
    }

    // PoiSync: this place holds something that is being carried (by anyone).
    internal static bool Involves(Transform place)
    {
        if (place == null) return false;
        if (_mine != null && PlaceOf(_mine) is Transform mine && mine.Pointer == place.Pointer) return true;
        foreach (var c in Remotes.Values)
            if (c.Co != null && PlaceOf(c.Co) is Transform t && t.Pointer == place.Pointer) return true;
        return false;
    }

    // Someone else has it: don't let the local player take it out of their arms.
    internal static bool CarriedByOther(CarriableObject co) => KeyOf(co) is string k && Remotes.ContainsKey(k);

    private static void SendPick(CoopSession session, int playerId, string key, CarriableObject co, int toPlayer)
    {
        _nextResend = Time.unscaledTime + ResendEvery;
        var body = PlayerSync.LocalBody;
        if (body == null) return;
        var t = Moving(co);
        var w = session.BeginMessage(MsgType.Carry);
        w.Put((byte)Kind.Pick);
        w.Put(playerId);
        w.Put(key);
        Put(w, body.InverseTransformPoint(t.position), Quaternion.Inverse(body.rotation) * t.rotation);
        MirrorSync.WriteAnimator(w, MirrorSync.AnimatorFor(co.ai));
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready)
        {
            if (session.Role != Role.Host) return;
            if (_mine != null) SendPick(session, session.LocalPlayerId, _mineKey, _mine, from.PlayerId);
            foreach (var (key, c) in Remotes)
            {
                if (c.Player == from.PlayerId) continue;
                var w = session.BeginMessage(MsgType.Carry);
                w.Put((byte)Kind.Pick); w.Put(c.Player); w.Put(key);
                Put(w, c.LocalPos, c.LocalRot);
                PutAnimator(w, c.AnimValues, c.AnimStates);
                session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            }
            return;
        }
        if (kind == Kind.Denied)
        {
            string denied = r.GetString();
            if (_mine == null || _mineKey != denied) return;
            Plugin.Logger.LogInfo($"Carry: {_mine.name} was taken first by someone else; putting it down");
            _quiet = true;
            try { _mine.StopCarrying(); }
            finally { _quiet = false; }
            LocalDropped(_mine);
            return;
        }

        int player = r.GetInt();
        if (session.Role == Role.Host) player = from.PlayerId;  // guests only speak for themselves
        string k = r.GetString();
        ReadPose(r, out var pos, out var rot);
        var (animValues, animStates) = kind == Kind.Pick ? MirrorSync.ReadAnimator(r) : (null, null);
        bool rests = kind == Kind.Roll && r.GetBool();
        if (player == session.LocalPlayerId) return;

        if (kind == Kind.Pick)
        {
            bool mineAlready = _mineKey == k;
            bool othersAlready = Remotes.TryGetValue(k, out var held) && held.Player != player;
            if (session.Role == Role.Host && (mineAlready || othersAlready))
            {
                var d = session.BeginMessage(MsgType.Carry);
                d.Put((byte)Kind.Denied);
                d.Put(k);
                session.SendTo(player, d, DeliveryMethod.ReliableOrdered);
                return;
            }
            if (!Remotes.TryGetValue(k, out var c))
            {
                Remotes[k] = c = new Remote { Player = player };
                Plugin.Logger.LogInfo($"Carry: player {player} picked up {k}");
            }
            c.Player = player;
            c.LocalPos = pos;
            c.LocalRot = rot;
            c.AnimValues = animValues;
            c.AnimStates = animStates;
        }
        else if (kind == Kind.Drop)
        {
            if (!Remotes.TryGetValue(k, out var c) || c.Player != player) return;
            Plugin.Logger.LogInfo($"Carry: player {player} put down {k} at {pos}");
            bool wasKinematic = c.WasKinematic, held = c.Held;
            var co = c.Co ?? Lookup(k);
            Release(k, pos, rot);
            // Held still until the dropper says where it went
            if (held && co != null && co.ai == null && co.rb != null)
            {
                co.rb.isKinematic = true;
                Follows[k] = new Follow { Co = co, WasKinematic = wasKinematic, Pos = pos, Rot = rot, Until = Time.unscaledTime + RollFor + 5f };
            }
        }
        else if (kind == Kind.Roll)
        {
            if (Follows.TryGetValue(k, out var f)) { f.Pos = pos; f.Rot = rot; }
            if (rests) Settle(k, pos, rot);
        }
        else return;

        if (session.Role == Role.Host)
        {
            var w = session.BeginMessage(MsgType.Carry);
            w.Put((byte)kind); w.Put(player); w.Put(k);
            Put(w, pos, rot);
            if (kind == Kind.Pick) PutAnimator(w, animValues, animStates);
            if (kind == Kind.Roll) w.Put(rests);
            session.Send(w, kind == Kind.Roll && !rests ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered, exceptPlayerId: player);
        }
    }

    private static void Put(NetDataWriter w, Vector3 p, Quaternion q)
    {
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
    }

    // Same layout as MirrorSync.WriteAnimator, from values already read.
    private static void PutAnimator(NetDataWriter w, float[] values, (int hash, float time)[] states)
    {
        values ??= new float[0];
        states ??= new (int, float)[0];
        w.Put((byte)values.Length);
        foreach (var v in values) w.Put(System.BitConverter.HalfToInt16Bits((System.Half)v));
        w.Put((byte)states.Length);
        foreach (var (hash, time) in states) { w.Put(hash); w.Put(time); }
    }

    private static void ReadPose(NetDataReader r, out Vector3 p, out Quaternion q)
    {
        p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        q = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
    }

    public static string Describe()
    {
        var parts = new List<string>();
        if (_mine != null) parts.Add($"me: {_mine.name} ({_mineKey})");
        foreach (var (key, c) in Remotes) parts.Add($"player {c.Player}: {(c.Co != null ? c.Co.name : "?")} ({key}) held={c.Held}");
        return parts.Count == 0 ? "nothing carried" : string.Join(" | ", parts);
    }
}

[HarmonyPatch(typeof(CarriableObject))]
internal static class CarryHooks
{
    [HarmonyPrefix, HarmonyPatch(nameof(CarriableObject.StartCarrying))]
    private static bool StartPre(CarriableObject __instance)
    {
        if (CarrySync.CarriedByOther(__instance)) return false;
        MirrorSync.Release(__instance.ai);  // a mirrored hare gets its own AI back before the game carries it
        return true;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CarriableObject.StartCarrying))]
    private static void StartPost(CarriableObject __instance) { _starting = false; CarrySync.LocalPicked(__instance); }

    // A place's saved state loaded from another player can say "carried"; the game then puts the object back in the
    // local player's hands, at once or a little later. Only a pick-up started here, or loading our own save, may.
    private static bool _starting;

    [HarmonyPrefix, HarmonyPatch(nameof(CarriableObject.StartCarrying)), HarmonyPriority(Priority.Last)]
    private static void Starting() => _starting = true;

    [HarmonyPrefix, HarmonyPatch(nameof(CarriableObject.AttachToPlayer))]
    private static bool Attach(CarriableObject __instance)
    {
        if (_starting || CoopDriver.Session is not { IsConnected: true } || SaveLoadGame.isLoading) return true;
        Plugin.Logger.LogInfo($"Carry: kept {__instance.name} out of my hands (someone else's place state said carried)");
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CarriableObject.StopCarrying), new Type[0])]
    private static void Stop0(CarriableObject __instance) => CarrySync.LocalDropped(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(CarriableObject.StopCarrying), new[] { typeof(Vector3), typeof(Quaternion) })]
    private static void StopAt(CarriableObject __instance) => CarrySync.LocalDropped(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(CarriableObject.StopCarrying), new[] { typeof(Transform) })]
    private static void StopOn(CarriableObject __instance) => CarrySync.LocalDropped(__instance);
}
