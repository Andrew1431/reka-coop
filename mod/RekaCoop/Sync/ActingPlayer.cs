using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Host: an NPC's AI sees "the player" through the game's shared references (TransformReference assets the host's own
// body and head are assigned to), so it only ever noticed, walked up to and talked to the host. While one AI runs its
// update those references point at whichever player is nearest to that AI, a guest's puppet included, and back at the
// host's player the moment it returns. What the AI then starts (a talk with its callbacks, a walk to the player) is the
// host's own story, set off by that guest (PLAN.md, rule 4). Nothing else runs inside an AI's update, so everything
// else that reads those references (culling, sound, fog, the camera) still sees the host's player.
// The same goes for a detector ticked for a guest and for a guest's intent (Enter, Exit).
// (GeneralAIController.s_playerRef is not what its PlayerTransform and PlayerHeadTransform read: replacing it changed
// nothing.)
[HarmonyPatch]
internal static class ActingPlayer
{
    private static readonly List<TransformReference> BodyRefs = new(), HeadRefs = new();
    private static Transform _body, _head;       // the host's own
    private static float _nextScan;
    private static bool _active, _swapped;
    private static int _depth;
    private const float Closer = 1f;  // a guest must be this much nearer than the host to be the one an AI sees
    private static readonly Dictionary<int, Transform> Heads = new();  // puppet root -> its head bone

    // The guest the AI being updated right now sees as the player; -1: the host, or no AI is being updated.
    internal static int Current { get; private set; } = -1;
    internal static bool InAi => _depth > 0;
    private static int _swaps;  // dev
    internal static string Trace;
    private static float _nextTrace;
    private static string _last = "";

    public static void Tick(CoopSession session)
    {
        if (_swapped) Restore();  // an update that never returned
        _depth = 0;
        _active = false;
        if (session.Role != Role.Host || !session.IsConnected || !PlayerSync.AnyPuppetInWorld) return;
        Transform body, head;
        try { body = GeneralAIController.PlayerTransform; head = GeneralAIController.PlayerHeadTransform; }
        catch { return; }
        if (body == null) return;
        if (_body == null || body.Pointer != _body.Pointer || _head == null != (head == null) || Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 5f;
            int before = BodyRefs.Count + HeadRefs.Count;
            _body = body; _head = head;
            BodyRefs.Clear(); HeadRefs.Clear();
            foreach (var r in Resources.FindObjectsOfTypeAll<TransformReference>())
            {
                var t = r.transform;
                if (t == null) continue;
                if (t.Pointer == body.Pointer) BodyRefs.Add(r);
                else if (head != null && t.Pointer == head.Pointer) HeadRefs.Add(r);
            }
            if (before != BodyRefs.Count + HeadRefs.Count)
                Plugin.Logger.LogInfo($"Acting player: AIs see the nearest player ({Describe()})");
        }
        _active = BodyRefs.Count > 0;
    }

    private static Transform HeadOf(Transform puppet)
    {
        if (_head == null) return puppet;
        int id = puppet.GetInstanceID();
        if (Heads.TryGetValue(id, out var h) && h != null) return h;
        h = puppet;
        foreach (var t in puppet.GetComponentsInChildren<Transform>(true)) if (t.name == _head.name) { h = t; break; }
        return Heads[id] = h;
    }

    private static void Point(Transform body, Transform head)
    {
        foreach (var r in BodyRefs) if (r != null) r.transform = body;
        foreach (var r in HeadRefs) if (r != null) r.transform = head;
    }

    private static void Restore()
    {
        _swapped = false;
        Current = -1;
        if (_body != null) Point(_body, _head);
    }

    private static void Before(GeneralAIController ai)
    {
        if (Trace != null && ai != null && Time.unscaledTime >= _nextTrace && ai.name.Contains(Trace))
        {
            _nextTrace = Time.unscaledTime + 1f;
            var p = PlayerSync.NearestPuppetInWorld(ai.transform.position, out int pid);
            Plugin.Logger.LogInfo($"[actor] {ai.name} at {ai.transform.position} active={_active} depth={_depth} puppet {pid} {(p != null ? (p.position - ai.transform.position).magnitude : -1f):0.0}m host {(_body != null ? (_body.position - ai.transform.position).magnitude : -1f):0.0}m target={(ai.currentTarget != null ? ai.currentTarget.name : "null")} observes={(ai.observationTarget != null ? ai.observationTarget.name : "null")}");
        }
        if (!_active || _depth++ > 0 || ai == null || _body == null) return;
        var at = ai.transform.position;
        var puppet = PlayerSync.NearestPuppetInWorld(at, out int id);
        if (puppet == null) return;
        if ((puppet.position - at).magnitude + Closer >= (_body.position - at).magnitude) return;
        Point(puppet, HeadOf(puppet));
        _swapped = true;
        Current = id;
        _swaps++;
        _last = ai.name;
    }

    // Host: what runs until Exit is this guest's doing (their intent, a detector they walked into): the game's player
    // references point at their puppet. False (and no Exit owed) when it can't be done.
    internal static bool Enter(int playerId)
    {
        if (!_active || _body == null) return false;
        if (_depth > 0) { _depth++; return true; }
        var puppet = PlayerSync.PuppetRoot(playerId);
        if (puppet == null || !PlayerSync.InWorldNow(playerId)) return false;
        Point(puppet, HeadOf(puppet));
        _swapped = true;
        Current = playerId;
        _depth = 1;
        return true;
    }

    internal static void Exit() => After();

    private static void After()
    {
        if (!_active || --_depth > 0) return;
        _depth = 0;
        if (_swapped) Restore();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.Update))]
    private static void UpdatePre(GeneralAIController __instance) => Before(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.Update))]
    private static void UpdatePost() => After();

    [HarmonyPrefix, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.LateUpdate))]
    private static void LatePre(GeneralAIController __instance) => Before(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.LateUpdate))]
    private static void LatePost() => After();

    [HarmonyPrefix, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.TickUpdate))]
    private static void TickPre(GeneralAIController __instance) => Before(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.TickUpdate))]
    private static void TickPost() => After();

    [HarmonyPrefix, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.TickUpdateSideSystems))]
    private static void SidePre(GeneralAIController __instance) => Before(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.TickUpdateSideSystems))]
    private static void SidePost() => After();

    private static string Path(Transform t)
    {
        if (t == null) return "null";
        string s = t.name;
        for (var x = t.parent; x != null; x = x.parent) s = x.name + "/" + s;
        return $"{s} @{t.position}";
    }

    public static string Describe() =>
        $"active={_active} body refs=[{string.Join(", ", BodyRefs.Select(r => r.name))}] head refs=[{string.Join(", ", HeadRefs.Select(r => r.name))}] swaps={_swaps} last={_last}";

    // dev: what the AI's two getters give with the references pointed at the nearest puppet
    public static string Probe()
    {
        if (!_active) return "not active: " + Describe();
        var sb = new System.Text.StringBuilder(Describe());
        sb.Append($"\nhost: PlayerTransform={Path(GeneralAIController.PlayerTransform)}\n   PlayerHeadTransform={Path(GeneralAIController.PlayerHeadTransform)}");
        var puppet = PlayerSync.NearestPuppetInWorld(PlayerSync.LocalFeet ?? Vector3.zero, out _);
        if (puppet == null) return sb + "\nno puppet";
        Point(puppet, HeadOf(puppet));
        try { sb.Append($"\npuppet: PlayerTransform={Path(GeneralAIController.PlayerTransform)}\n   PlayerHeadTransform={Path(GeneralAIController.PlayerHeadTransform)}"); }
        finally { Restore(); }
        return sb.ToString();
    }
}
