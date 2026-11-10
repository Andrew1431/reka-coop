using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;

namespace RekaCoop.Sync;

// Places keep small true/false conditions (GenericPOICondition) that their steps add up: each of the Lutki's four
// loose planks sets its own when it's picked up, and the Lutki stands once all four are set. A pickup only runs on the
// picker's copy (the others just see it vanish), so with two players neither copy had all four. Any copy that sets
// one sends it; the others set theirs the same way, so their own listeners run too. The host passes them on.
// The tutorial's yard gate (Tutorial_FenceGateLock) is the same kind of thing: it closes behind whoever walks in, on
// their copy only. A close or open is sent too; on a close the others are brought in beside the player who closed it,
// so the group stays together.
public static class ConditionSync
{
    private enum Kind : byte { Condition = 1, GateClose = 2, GateOpen = 3, Doors = 4, DoorsReady = 5 }

    internal static bool Replaying;
    private static CoopSession _session;
    private static readonly Dictionary<string, bool> Last = new();   // path -> last value set here, by anyone

    public static void Attach(CoopSession session)
    {
        _session = session;
        session.Message += (from, type, r) => { if (type == MsgType.Condition) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        Last.Clear();
        _locks = null;
        _doorsSent.Clear();
        _doorsReadySent = false;
    }

    // Doors the story unlocks (the old woman's hut: Tutorial_BabaHutLock opens its door on a story event, which only
    // the host's copy hears). The host's are the ones that count: open and locked go to guests when they change, and
    // all of them to a guest who has just arrived.
    private static Tutorial_BabaHutLock[] _locks;
    private static readonly Dictionary<string, (bool open, bool locked)> _doorsSent = new();
    private static bool _doorsReadySent;
    private static float _nextDoors;

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || UnityEngine.Time.unscaledTime < _nextDoors) return;
        _nextDoors = UnityEngine.Time.unscaledTime + 1f;
        if (session.Role == Role.Client)
        {
            if (_doorsReadySent || !WorldSync.IsGuest) return;
            _doorsReadySent = true;
            var w = session.BeginMessage(MsgType.Condition);
            w.Put((byte)Kind.DoorsReady);
            session.Send(w, DeliveryMethod.ReliableOrdered);
            return;
        }
        var changed = Doors().Where(d => !_doorsSent.TryGetValue(d.path, out var was) || was != (d.open, d.locked)).ToList();
        if (changed.Count == 0) return;
        bool first = _doorsSent.Count == 0;
        foreach (var d in changed) _doorsSent[d.path] = (d.open, d.locked);
        if (first) return;   // as loaded: a guest has that from the world, or asks
        SendDoors(session, changed, -1);
    }

    private static List<(string path, bool open, bool locked)> Doors()
    {
        if (_locks == null || _locks.Any(l => l == null))
            _locks = UnityEngine.Object.FindObjectsByType<Tutorial_BabaHutLock>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None).ToArray();
        var all = new List<(string, bool, bool)>();
        foreach (var l in _locks)
            if (l != null && l.gate != null) all.Add((ScenePath.Of(l.gate.transform), l.gate._isOpen, l.gate.isLocked));
        return all;
    }

    private static void SendDoors(CoopSession session, List<(string path, bool open, bool locked)> doors, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Condition);
        w.Put((byte)Kind.Doors);
        w.Put(doors.Count);
        foreach (var d in doors) { w.Put(d.path); w.Put(d.open); w.Put(d.locked); }
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Conditions: sent {doors.Count} doors: {string.Join(", ", doors.Select(d => $"{d.path.Split('/').Last()} open={d.open} locked={d.locked}"))}");
    }

    private static void OnDoors(NetDataReader r)
    {
        int n = r.GetInt();
        for (int i = 0; i < n; i++)
        {
            string path = r.GetString();
            bool open = r.GetBool(), locked = r.GetBool();
            var hinge = ScenePath.Resolve(path)?.GetComponent<AnimatedHingeJoint>();
            if (hinge == null) { Plugin.Logger.LogInfo($"Conditions: no door {path} here"); continue; }
            if (hinge._isOpen == open && hinge.isLocked == locked) continue;
            try
            {
                if (hinge.isLocked && !locked) hinge.Unlock();
                if (hinge._isOpen != open) { if (open) hinge.Open(); else hinge.Close(); }
                if (!hinge.isLocked && locked) hinge.Lock();
                Plugin.Logger.LogInfo($"Conditions: the host's door {path.Split('/').Last()} is open={open} locked={locked}; set here");
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"Conditions: door {path} failed: {e.Message}"); }
        }
    }

    internal static void LocalSet(GenericPOICondition c, bool value)
    {
        if (Replaying || c == null) return;
        var path = ScenePath.Of(c.transform);
        Last[path] = value;
        if (_session == null || !_session.IsConnected) return;
        Send(_session, path, value, -1);
    }

    internal static void LocalGate(Tutorial_FenceGateLock gate, bool open)
    {
        if (Replaying || gate == null || _session == null || !_session.IsConnected) return;
        if (WorldSync.IsGuest) return;   // the host's gate is the one that counts; it tells us
        var me = Find.One<PlayerMovement_2021>()?.transform;
        var w = _session.BeginMessage(MsgType.Condition);
        w.Put((byte)(open ? Kind.GateOpen : Kind.GateClose));
        // Host: the story may be running for a guest (they walked into the yard): they are the one who closed it.
        int who = _session.LocalPlayerId;
        var at = me != null ? me.position : UnityEngine.Vector3.zero;
        int actor = _session.Role == Role.Host ? ActingPlayer.Current : -1;
        var puppet = actor >= 0 ? PlayerSync.PuppetRoot(actor) : null;
        if (puppet != null) { who = actor; at = puppet.position; }
        w.Put(who);
        w.Put(ScenePath.Of(gate.transform));
        w.Put(at.x); w.Put(at.y); w.Put(at.z);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Conditions: gate {(open ? "opened" : "closed")} here{(puppet != null ? " for " + NameTags.NameOf(who) : "")}");
        if (puppet != null && !open) BringIn(gate, who, at);
    }

    // In beside whoever closed the gate, so it doesn't close with us outside. Which side of the gate counts, not how
    // far: standing just outside it (under 3 m from the closer) used to leave us out.
    private static void BringIn(Tutorial_FenceGateLock gate, int player, UnityEngine.Vector3 at)
    {
        var me = Find.One<PlayerMovement_2021>();
        var g = gate.transform;
        // The gate closes once the closer is a few steps in: inwards is from the gate to them. (The door's own axes
        // were used before; they don't say which way is in, and a host standing outside was left there.)
        var across = at - g.position;
        // The yard's own "inside the fence" area says it best; the closer may still be in the gateway.
        var siblings = g.parent;
        for (int i = 0; siblings != null && i < siblings.childCount; i++)
        {
            var c = siblings.GetChild(i);
            if (c.name.Contains("Inside") && c.GetComponent<PlayerTrigger>() != null) { across = c.position - g.position; break; }
        }
        across.y = 0;
        if (me == null || across.magnitude < 0.5f) { Plugin.Logger.LogInfo($"Conditions: can't tell which way is in at the gate (me {(me != null)}, closer {at}, gate {g.position})"); return; }
        across.Normalize();
        var mine = me.transform.position - g.position;
        if (UnityEngine.Vector3.Dot(mine, across) > 0.3f && (me.transform.position - at).magnitude <= 15f) { Plugin.Logger.LogInfo("Conditions: already in the yard"); return; }
        var to = g.position + across * 3f;  // a few steps in
        to.y = at.y;
        var rb = me.GetComponent<UnityEngine.Rigidbody>();
        if (rb != null) rb.position = to;
        me.transform.position = to;
        Plugin.Logger.LogInfo($"Conditions: moved in beside {NameTags.NameOf(player)} at the gate");
    }

    private static void OnGate(CoopSession session, NetDataReader r, Kind kind)
    {
        int player = r.GetInt();
        var path = r.GetString();
        var at = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        if (session.Role == Role.Host)
        {
            var w = session.BeginMessage(MsgType.Condition);
            w.Put((byte)kind); w.Put(player); w.Put(path); w.Put(at.x); w.Put(at.y); w.Put(at.z);
            session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: player);
        }
        var gate = ScenePath.Resolve(path)?.GetComponent<Tutorial_FenceGateLock>();
        if (gate == null) { Plugin.Logger.LogInfo($"Conditions: no gate {path} here"); return; }
        if (kind == Kind.GateClose && player != session.LocalPlayerId) BringIn(gate, player, at);
        Replaying = true;
        try
        {
            if (kind == Kind.GateClose) gate.Close();
            else gate.Open();
            Plugin.Logger.LogInfo($"Conditions: {NameTags.NameOf(player)} {(kind == Kind.GateClose ? "closed" : "opened")} the gate");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Conditions: gate failed: {e.Message}"); }
        finally { Replaying = false; }
    }

    private static void Send(CoopSession session, string path, bool value, int except)
    {
        var w = session.BeginMessage(MsgType.Condition);
        w.Put((byte)Kind.Condition);
        w.Put(path);
        w.Put(value);
        session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: except);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.DoorsReady) { if (session.Role == Role.Host && from != null) SendDoors(session, Doors(), from.PlayerId); return; }
        if (kind == Kind.Doors) { if (session.Role == Role.Client) OnDoors(r); return; }
        if (kind != Kind.Condition) { OnGate(session, r, kind); return; }
        var path = r.GetString();
        bool value = r.GetBool();
        if (session.Role == Role.Host) Send(session, path, value, from.PlayerId);
        if (Last.TryGetValue(path, out var was) && was == value) return;
        var c = ScenePath.Resolve(path)?.GetComponent<GenericPOICondition>();
        if (c == null) { Plugin.Logger.LogInfo($"Conditions: no {path} here"); return; }
        Last[path] = value;
        Replaying = true;
        try
        {
            if (value) c.SetConditionTrue();
            else c.SetConditionFalse();
            Plugin.Logger.LogInfo($"Conditions: {NameTags.NameOf(from.PlayerId)} set {path} {value}");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Conditions: setting {path} failed: {e.Message}"); }
        finally { Replaying = false; }
    }
}

[HarmonyPatch(typeof(GenericPOICondition))]
internal static class ConditionHooks
{
    [HarmonyPostfix, HarmonyPatch(nameof(GenericPOICondition.SetConditionTrue))]
    private static void True(GenericPOICondition __instance) => ConditionSync.LocalSet(__instance, true);

    [HarmonyPostfix, HarmonyPatch(nameof(GenericPOICondition.SetConditionFalse))]
    private static void False(GenericPOICondition __instance) => ConditionSync.LocalSet(__instance, false);

    [HarmonyPostfix, HarmonyPatch(typeof(Tutorial_FenceGateLock), nameof(Tutorial_FenceGateLock.Close))]
    private static void GateClose(Tutorial_FenceGateLock __instance) => ConditionSync.LocalGate(__instance, false);

    [HarmonyPostfix, HarmonyPatch(typeof(Tutorial_FenceGateLock), nameof(Tutorial_FenceGateLock.Open))]
    private static void GateOpen(Tutorial_FenceGateLock __instance) => ConditionSync.LocalGate(__instance, true);
}

// What opens and closes the yard gate, on each copy (1.0.32 added a lock that opens after the journal).
[HarmonyPatch]
internal static class GateTrace
{
    private static void Log(string what, UnityEngine.Component c) =>
        Plugin.Logger.LogInfo($"[gate] {what} {ScenePath.Of(c.transform)}");

    [HarmonyPostfix, HarmonyPatch(typeof(Tutorial_FenceGateLock), nameof(Tutorial_FenceGateLock.TryOpen))]
    private static void TryOpen(Tutorial_FenceGateLock __instance) => Log($"TryOpen (pending={__instance.openPending} completed={__instance.isCompleted})", __instance);

    [HarmonyPostfix, HarmonyPatch(typeof(Tutorial_FenceGateLock), nameof(Tutorial_FenceGateLock.OpenAfterJournal))]
    private static void AfterJournal(Tutorial_FenceGateLock __instance) => Log("OpenAfterJournal", __instance);

    [HarmonyPostfix, HarmonyPatch(typeof(Tutorial_FenceGateLock), nameof(Tutorial_FenceGateLock.OnLoadedInternal))]
    private static void Loaded(Tutorial_FenceGateLock __instance, bool loadedAsCompleted) => Log($"loaded completed={loadedAsCompleted}", __instance);

    [HarmonyPostfix, HarmonyPatch(typeof(AnimatedHingeJoint), nameof(AnimatedHingeJoint.Open))]
    private static void HingeOpen(AnimatedHingeJoint __instance) { if (__instance.name == "FenceDoor") Log($"hinge Open (locked={__instance.isLocked})", __instance); }

    [HarmonyPostfix, HarmonyPatch(typeof(AnimatedHingeJoint), nameof(AnimatedHingeJoint.Close))]
    private static void HingeClose(AnimatedHingeJoint __instance) { if (__instance.name == "FenceDoor") Log("hinge Close", __instance); }

    [HarmonyPostfix, HarmonyPatch(typeof(AnimatedHingeJoint), nameof(AnimatedHingeJoint.Unlock))]
    private static void HingeUnlock(AnimatedHingeJoint __instance)
    {
        if (__instance.name != "FenceDoor") return;
        string stack;
        try { stack = new Il2CppSystem.Diagnostics.StackTrace().ToString().Replace("\n", " < "); }
        catch (System.Exception e) { stack = e.GetType().Name; }
        Log($"hinge Unlock at t={UnityEngine.Time.time:0.0} from {stack}", __instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(AnimatedHingeJoint), nameof(AnimatedHingeJoint.Lock))]
    private static void HingeLock(AnimatedHingeJoint __instance) { if (__instance.name == "FenceDoor") Log("hinge Lock", __instance); }
}
