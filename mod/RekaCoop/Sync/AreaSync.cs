using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using RekaCoop.Net;
using UnityEngine;
using UnityEngine.Events;

namespace RekaCoop.Sync;

// PlayerTrigger: a physics trigger that runs events when the local player's colliders walk in or out. Most of what
// hangs on one is for that player alone (the indoor camera and sound, a compass entry, a prompt) and stays as it is on
// every copy. The rest is story (the old woman greets whoever comes up to her yard gate; the yard notices who is in
// it). A puppet has no such colliders, so the host's copy never noticed a guest, and a guest's copy ran that story by
// itself. On a guest the story calls of a trigger are switched off at the source the first time the trigger is
// touched, and what would have run goes to the host as an intent (PLAN.md, rule 2); the host runs its own copy's story
// calls with that guest as the acting player. On the host a trigger's story is entered when the first player walks in
// and left when the last one walks out, whoever they are.
[HarmonyPatch]
internal static class AreaSync
{
    // Calls that are for the player standing there. Anything else is story.
    private static readonly HashSet<string> LocalTargets = new()
    {
        "FMODTriggerIndoorSnapshots", "PlayerEventInvoker", "POI_VolumeActivator", "SetGlobalFMODParam", "ManageCollectTutorialPrompts",
        "TriggerWarningHelper", "RegisterLocationOnCompass", "POISoundTriggerer", "StudioEventEmitter", "PoiQuestInterface",
    };

    private sealed class Info
    {
        public PlayerTrigger Trigger;
        public readonly List<(UnityEvent ev, int i)> Story = new(), Local = new();
        public bool Silenced;   // guest: story calls are off and the two intent listeners are on
    }

    private static readonly Dictionary<int, Info> Known = new();
    private static readonly Dictionary<int, HashSet<int>> Inside = new();          // host: trigger -> guests in it
    private static Info _held;
    private static int _depth;
    private static float _nextSweep;

    private static void Sort(Info info, UnityEvent ev)
    {
        if (ev == null) return;
        for (int i = 0; i < ev.GetPersistentEventCount(); i++)
        {
            var target = ev.GetPersistentTarget(i);
            if (target == null) continue;
            (LocalTargets.Contains(target.GetIl2CppType().Name) ? info.Local : info.Story).Add((ev, i));
        }
    }

    private static Info Of(PlayerTrigger t)
    {
        int id = t.GetInstanceID();
        if (Known.TryGetValue(id, out var info)) return info;
        info = new Info { Trigger = t };
        try { Sort(info, t.OnPlayerEnter); Sort(info, t.OnPlayerExit); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Areas: can't read {t.name}: {e.Message}"); }
        return Known[id] = info;
    }

    private static void Switch(List<(UnityEvent ev, int i)> calls, bool on)
    {
        foreach (var (ev, i) in calls)
            if (ev != null) ev.SetPersistentListenerState(i, on ? UnityEventCallState.RuntimeOnly : UnityEventCallState.Off);
    }

    private static bool GuestsIn(PlayerTrigger t) => Inside.TryGetValue(t.GetInstanceID(), out var s) && s.Count > 0;

    private static void Begin(PlayerTrigger t)
    {
        if (t == null || _depth++ > 0) return;
        _held = null;
        var session = CoopDriver.Session;
        if (session == null || !session.IsConnected) return;
        if (WorldSync.IsGuest)
        {
            var info = Of(t);
            if (info.Silenced || info.Story.Count == 0) return;
            info.Silenced = true;
            Switch(info.Story, false);
            t.OnPlayerEnter?.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new System.Action(() => Tell(info, true))));
            t.OnPlayerExit?.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new System.Action(() => Tell(info, false))));
            return;
        }
        // Host: while a guest is in it, the host's own coming and going changes nothing in the story.
        if (session.Role != Role.Host || !GuestsIn(t)) return;
        _held = Of(t);
        Switch(_held.Story, false);
    }

    private static void End()
    {
        if (_depth == 0 || --_depth > 0 || _held == null) return;
        var info = _held;
        _held = null;
        if (info.Trigger != null) Switch(info.Story, true);
    }

    // Guest: this copy's player walked in or out of a trigger with story on it.
    private static void Tell(Info info, bool enter)
    {
        if (!info.Silenced || info.Trigger == null || !WorldSync.IsGuest) return;
        Intents.Send(enter ? Intents.Kind.AreaEnter : Intents.Kind.AreaExit, info.Trigger);
    }

    // Host: a guest walked into or out of their copy of this trigger. Only its story runs here.
    internal static void Run(Transform at, int player, bool enter)
    {
        var t = at.GetComponent<PlayerTrigger>();
        if (t == null) return;
        int id = t.GetInstanceID();
        if (!Inside.TryGetValue(id, out var set)) Inside[id] = set = new HashSet<int>();
        bool hostIn = t.howManyCollidersOfMineIsPlayerIn > 0;
        string who = NameTags.NameOf(player), path = ScenePath.Of(at);
        if (enter ? !set.Add(player) : !set.Remove(player)) return;
        bool others = enter ? set.Count > 1 || hostIn : set.Count > 0 || hostIn;
        string did = enter ? "walked into" : "walked out of";
        if (others || !t.isActiveAndEnabled) { Plugin.Logger.LogInfo($"Areas: {who} {did} {path} (someone else is in it, or off here)"); return; }
        Plugin.Logger.LogInfo($"Areas: {who} {did} {path}");
        var info = Of(t);
        if (enter) DialogueSync.NoteRequest(player, 10f);
        Switch(info.Local, false);
        try { (enter ? t.OnPlayerEnter : t.OnPlayerExit)?.Invoke(); }
        finally { Switch(info.Local, true); }
    }

    public static void Tick(CoopSession session)
    {
        if (Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + 1f;
        // No longer a guest: this copy's triggers run their own story again.
        if (!WorldSync.IsGuest && Known.Count > 0 && Known.Values.Any(k => k.Silenced))
            foreach (var info in Known.Values.Where(k => k.Silenced))
            {
                info.Silenced = false;
                if (info.Trigger != null) Switch(info.Story, true);
            }
        if (Known.Count > 500) foreach (int id in Known.Where(k => k.Value.Trigger == null).Select(k => k.Key).ToArray()) Known.Remove(id);
        if (Inside.Count == 0) return;
        if (session.Role != Role.Host || !session.IsConnected) { Inside.Clear(); return; }
        // A guest who left the session or the world has left every trigger too.
        foreach (int id in Inside.Keys.ToArray())
        {
            var t = Known.TryGetValue(id, out var info) ? info.Trigger : null;
            if (t == null) { Inside.Remove(id); continue; }
            foreach (int player in Inside[id].ToArray())
                if (!PlayerSync.InWorldNow(player)) Run(t.transform, player, false);
        }
    }

    // dev
    public static string Describe(PlayerTrigger t)
    {
        var info = Of(t);
        string Names(List<(UnityEvent ev, int i)> l) => string.Join(",", l.Select(c => c.ev.GetPersistentTarget(c.i).GetIl2CppType().Name + "." + c.ev.GetPersistentMethodName(c.i)));
        return $"story=[{Names(info.Story)}] local=[{Names(info.Local)}] silenced={info.Silenced} playerIn={t.howManyCollidersOfMineIsPlayerIn} guestsIn={(Inside.TryGetValue(t.GetInstanceID(), out var s) ? s.Count : 0)}";
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerEnter))]
    private static void EnterPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerEnter))]
    private static void EnterPost() => End();

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerExit))]
    private static void ExitPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerExit))]
    private static void ExitPost() => End();

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.CheckForPlayer))]
    private static void CheckPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.CheckForPlayer))]
    private static void CheckPost() => End();

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.FireHeldExit))]
    private static void HeldPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.FireHeldExit))]
    private static void HeldPost() => End();

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnDisable))]
    private static void DisablePre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnDisable))]
    private static void DisablePost() => End();
}
