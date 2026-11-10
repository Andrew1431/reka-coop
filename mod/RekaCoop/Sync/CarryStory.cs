using System.Collections.Generic;
using HarmonyLib;
using RekaCoop.Net;
using UnityEngine;
using UnityEngine.Events;

namespace RekaCoop.Sync;

// What picking something up or putting it down sets off besides the carrying itself. A carriable's OnPickUp / OnDrop
// events mix two things: calls for the thing and its carrier (its own AI, a sound) and story (a chicken in someone's
// arms switches the old woman's lines from "you brought no chicken" to "you brought one"). Host-first: on a guest the
// story calls are switched off the first time it lifts the thing, and the host, which hears of every pickup and drop
// through CarrySync, runs the story calls of its own copy for that guest. Story that listens to the local player's
// carry events instead (Tutorial_RitualSiteTrigger) hears about a guest's carrying the same way and doesn't run on guests.
internal static class CarryStory
{
    private sealed class Split
    {
        public readonly List<(UnityEvent ev, int i)> Story = new(), Local = new();
        public bool Silenced;
    }

    private static readonly Dictionary<int, Split> Known = new();
    private static bool _telling;
    private static bool IsHost => CoopDriver.Session?.Role == Role.Host;

    // Story unless it is for the carrier (sound and the like) or a component of the carried thing itself, which
    // CarrySync already keeps in step. Switching a GameObject on or off is always story: the host's switches reach guests.
    private static void Sort(Split s, CarriableObject co, UnityEvent ev)
    {
        if (ev == null) return;
        for (int i = 0; i < ev.GetPersistentEventCount(); i++)
        {
            var target = ev.GetPersistentTarget(i);
            if (target == null) continue;
            var comp = target.TryCast<Component>();
            bool local = comp != null && (AreaSync.IsLocalTarget(comp.GetIl2CppType().Name) || comp.transform.IsChildOf(co.transform));
            (local ? s.Local : s.Story).Add((ev, i));
        }
    }

    private static Split Of(CarriableObject co)
    {
        int id = co.GetInstanceID();
        if (Known.TryGetValue(id, out var s)) return s;
        if (Known.Count > 400) Known.Clear();
        s = new Split();
        try { Sort(s, co, co.OnPickUp); Sort(s, co, co.OnFirstPickUp); Sort(s, co, co.OnDrop); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: can't read {co.name}'s events: {e.Message}"); }
        return Known[id] = s;
    }

    private static void Switch(List<(UnityEvent ev, int i)> calls, bool on)
    {
        foreach (var (ev, i) in calls)
            if (ev != null) ev.SetPersistentListenerState(i, on ? UnityEventCallState.RuntimeOnly : UnityEventCallState.Off);
    }

    // Guest: about to lift this. Its story calls stay off from here on; the host runs its own.
    internal static void LocalLifting(CarriableObject co)
    {
        if (!WorldSync.IsGuest || co == null) return;
        var s = Of(co);
        if (s.Silenced || s.Story.Count == 0) return;
        s.Silenced = true;
        Switch(s.Story, false);
    }

    // Host: a guest picked this up.
    internal static void GuestPicked(CarriableObject co, int player)
    {
        if (!IsHost || co == null) return;
        Run(co, player, pick: true);
        if (CarrySync.Mine == null) Tell(co, default);
    }

    // Host: a guest put this down; `still` is what a guest still carries, if anything.
    internal static void GuestDropped(CarriableObject co, int player, Vector3 at, CarriableObject still)
    {
        if (!IsHost) return;
        if (co != null) Run(co, player, pick: false);
        if (CarrySync.Mine == null) Tell(still, at);
    }

    private static void Run(CarriableObject co, int player, bool pick)
    {
        var s = Of(co);
        if (s.Story.Count == 0) return;
        DialogueSync.NoteRequest(player, 10f);
        Switch(s.Local, false);
        try
        {
            if (pick)
            {
                if (!co.hasBeenCarried) { co.hasBeenCarried = true; co.OnFirstPickUp?.Invoke(); }
                co.OnPickUp?.Invoke();
            }
            else co.OnDrop?.Invoke();
            Plugin.Logger.LogInfo($"Carry: ran {co.name}'s story for player {player} {(pick ? "picking it up" : "putting it down")} ({s.Story.Count} story calls, {s.Local.Count} local)");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: {co.name}'s story failed: {e.Message}"); }
        finally { Switch(s.Local, true); }
    }

    private static void Tell(CarriableObject co, Vector3 droppedAt)
    {
        _telling = true;
        try
        {
            foreach (var t in Object.FindObjectsByType<Tutorial_RitualSiteTrigger>(FindObjectsSortMode.None))
            {
                if (t == null) continue;
                if (co != null) t.OnStartCarryingObject(co);
                else t.OnStopCarryingObject(droppedAt);
            }
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: telling the ritual site failed: {e}"); }
        finally { _telling = false; }
    }

    // dev
    internal static string Describe(CarriableObject co)
    {
        var s = Of(co);
        string Names(List<(UnityEvent ev, int i)> l) => string.Join(",", l.ConvertAll(c => c.ev.GetPersistentTarget(c.i).name + "." + c.ev.GetPersistentMethodName(c.i)));
        return $"story=[{Names(s.Story)}] local=[{Names(s.Local)}] silenced={s.Silenced}";
    }

    [HarmonyPatch(typeof(Tutorial_RitualSiteTrigger))]
    private static class Hooks
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Tutorial_RitualSiteTrigger.OnStartCarryingObject))]
        private static bool Start() => !WorldSync.IsGuest;

        [HarmonyPrefix, HarmonyPatch(nameof(Tutorial_RitualSiteTrigger.OnStopCarryingObject))]
        private static bool Stop() => !WorldSync.IsGuest;

        // Host put its own thing down while a guest still carries something: the story goes on with the guest's.
        [HarmonyPostfix, HarmonyPatch(nameof(Tutorial_RitualSiteTrigger.OnStopCarryingObject))]
        private static void Stopped(Tutorial_RitualSiteTrigger __instance)
        {
            if (_telling || !IsHost) return;
            var co = CarrySync.CarriedByAGuest();
            if (co != null) __instance.OnStartCarryingObject(co);
        }
    }
}
