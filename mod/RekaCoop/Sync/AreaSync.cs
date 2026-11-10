using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using RekaCoop.Net;
using UnityEngine;
using UnityEngine.Events;

namespace RekaCoop.Sync;

// PlayerTrigger: a physics trigger that runs story events when the local player's colliders walk in or out (the old
// woman greets whoever comes up to her yard gate). A puppet has no such colliders, so the host's copy never noticed a
// guest. A guest's copy still counts its own player in and out, but its two events are swapped for empty ones while
// it does: what would have run goes to the host as an intent (PLAN.md, rule 2), and the host runs its own copy's
// events with that guest as the acting player. On the host a trigger is entered when the first player walks in and
// left when the last one walks out, whoever they are.
[HarmonyPatch]
internal static class AreaSync
{
    private static UnityEvent _enterProbe, _exitProbe, _savedEnter, _savedExit;
    private static bool _enterFired, _exitFired, _held;
    private static int _depth;
    private static readonly Dictionary<int, HashSet<int>> Inside = new();          // host: trigger -> guests in it
    private static readonly Dictionary<int, PlayerTrigger> Triggers = new();
    private static float _nextSweep;

    private static bool GuestsIn(PlayerTrigger t) => Inside.TryGetValue(t.GetInstanceID(), out var s) && s.Count > 0;

    private static void Begin(PlayerTrigger t)
    {
        if (t == null || _depth++ > 0) return;
        _held = false;
        var session = CoopDriver.Session;
        if (session == null || !session.IsConnected) return;
        // Guest: always. Host: while a guest is in it, the host's own coming and going changes nothing.
        if (!(WorldSync.IsGuest || session.Role == Role.Host && GuestsIn(t))) return;
        if (_enterProbe == null)
        {
            _enterProbe = new UnityEvent();
            _enterProbe.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new System.Action(() => _enterFired = true)));
            _exitProbe = new UnityEvent();
            _exitProbe.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new System.Action(() => _exitFired = true)));
        }
        _enterFired = _exitFired = false;
        _savedEnter = t.OnPlayerEnter; _savedExit = t.OnPlayerExit;
        t.OnPlayerEnter = _enterProbe; t.OnPlayerExit = _exitProbe;
        _held = true;
    }

    private static void End(PlayerTrigger t)
    {
        if (_depth == 0 || --_depth > 0 || !_held) return;
        _held = false;
        if (t == null) return;
        t.OnPlayerEnter = _savedEnter; t.OnPlayerExit = _savedExit;
        _savedEnter = _savedExit = null;
        if (!WorldSync.IsGuest) return;
        if (_enterFired) Intents.Send(Intents.Kind.AreaEnter, t);
        if (_exitFired) Intents.Send(Intents.Kind.AreaExit, t);
    }

    // Host: a guest walked into or out of their copy of this trigger.
    internal static void Run(Transform at, int player, bool enter)
    {
        var t = at.GetComponent<PlayerTrigger>();
        if (t == null) return;
        int id = t.GetInstanceID();
        if (!Inside.TryGetValue(id, out var set)) { Inside[id] = set = new HashSet<int>(); Triggers[id] = t; }
        bool hostIn = t.howManyCollidersOfMineIsPlayerIn > 0;
        string who = NameTags.NameOf(player), path = ScenePath.Of(at);
        if (enter)
        {
            bool first = set.Count == 0 && !hostIn;
            if (!set.Add(player)) return;
            if (!first || !t.isActiveAndEnabled) { Plugin.Logger.LogInfo($"Areas: {who} walked into {path} (already entered, or off here)"); return; }
            Plugin.Logger.LogInfo($"Areas: {who} walked into {path}");
            DialogueSync.NoteRequest(player, 10f);
            t.OnPlayerEnter?.Invoke();
        }
        else
        {
            if (!set.Remove(player)) return;
            if (set.Count > 0 || hostIn || !t.isActiveAndEnabled) { Plugin.Logger.LogInfo($"Areas: {who} walked out of {path} (someone is still in it, or off here)"); return; }
            Plugin.Logger.LogInfo($"Areas: {who} walked out of {path}");
            t.OnPlayerExit?.Invoke();
        }
    }

    // Host: a guest who left the session or the world has left every trigger too.
    public static void Tick(CoopSession session)
    {
        if (Inside.Count == 0 || Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + 1f;
        if (session.Role != Role.Host || !session.IsConnected) { Inside.Clear(); Triggers.Clear(); return; }
        foreach (int id in Inside.Keys.ToArray())
        {
            if (!Triggers.TryGetValue(id, out var t) || t == null) { Inside.Remove(id); Triggers.Remove(id); continue; }
            foreach (int player in Inside[id].ToArray())
                if (!PlayerSync.InWorldNow(player)) Run(t.transform, player, false);
        }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerEnter))]
    private static void EnterPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerEnter))]
    private static void EnterPost(PlayerTrigger __instance) => End(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerExit))]
    private static void ExitPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnTriggerExit))]
    private static void ExitPost(PlayerTrigger __instance) => End(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.CheckForPlayer))]
    private static void CheckPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.CheckForPlayer))]
    private static void CheckPost(PlayerTrigger __instance) => End(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.FireHeldExit))]
    private static void HeldPre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.FireHeldExit))]
    private static void HeldPost(PlayerTrigger __instance) => End(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnDisable))]
    private static void DisablePre(PlayerTrigger __instance) => Begin(__instance);
    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerTrigger), nameof(PlayerTrigger.OnDisable))]
    private static void DisablePost(PlayerTrigger __instance) => End(__instance);
}
