using System.Collections.Generic;
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
    private enum Kind : byte { Condition = 1, GateClose = 2, GateOpen = 3 }

    internal static bool Replaying;
    private static CoopSession _session;
    private static readonly Dictionary<string, bool> Last = new();   // path -> last value set here, by anyone

    public static void Attach(CoopSession session)
    {
        _session = session;
        session.Message += (from, type, r) => { if (type == MsgType.Condition) OnMessage(session, from, r); };
    }

    public static void ResetLocal() => Last.Clear();

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
        var me = Find.One<PlayerMovement_2021>()?.transform;
        var w = _session.BeginMessage(MsgType.Condition);
        w.Put((byte)(open ? Kind.GateOpen : Kind.GateClose));
        w.Put(_session.LocalPlayerId);
        w.Put(ScenePath.Of(gate.transform));
        var at = me != null ? me.position : UnityEngine.Vector3.zero;
        w.Put(at.x); w.Put(at.y); w.Put(at.z);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Conditions: gate {(open ? "opened" : "closed")} here");
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
        if (kind == Kind.GateClose)
        {
            // In beside them first, so the gate doesn't close with us outside. Which side of the gate counts, not how
            // far: standing just outside it (under 3 m from the closer) used to leave us out.
            var me = Find.One<PlayerMovement_2021>();
            var g = gate.transform;
            var box = gate.GetComponent<UnityEngine.BoxCollider>();
            var across = box != null && box.size.z < box.size.x ? g.forward : g.right;  // the door's thin side faces in and out
            float Side(UnityEngine.Vector3 p) => UnityEngine.Vector3.Dot(p - g.position, across);
            if (me != null && (UnityEngine.Mathf.Sign(Side(me.transform.position)) != UnityEngine.Mathf.Sign(Side(at)) || (me.transform.position - at).magnitude > 15f))
            {
                var to = at + across * UnityEngine.Mathf.Sign(Side(at)) * 1.2f;  // a step further in than the closer
                var rb = me.GetComponent<UnityEngine.Rigidbody>();
                if (rb != null) rb.position = to;
                me.transform.position = to;
                Plugin.Logger.LogInfo($"Conditions: moved in beside {NameTags.NameOf(player)} at the gate");
            }
        }
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
