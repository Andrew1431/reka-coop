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
    private static readonly HashSet<string> StoryTypes = new() { "RitualProgressionAdvancer", "GenericPOICondition", "StandaloneDialogue", "POI_Controller", "CompletableStation" };
    private static bool _telling;
    private static bool IsHost => CoopDriver.Session?.Role == Role.Host;

    // Story unless it is for the carrier (sound and the like) or a component of the carried thing itself, which
    // CarrySync already keeps in step. Switching a GameObject on or off is always story: the host's switches reach guests.
    private static void Sort(Split s, Transform own, UnityEvent ev)
    {
        if (ev == null) return;
        for (int i = 0; i < ev.GetPersistentEventCount(); i++)
        {
            var target = ev.GetPersistentTarget(i);
            if (target == null) continue;
            var comp = target.TryCast<Component>();
            string type = comp?.GetIl2CppType().Name;
            // On the carried thing itself only what moves the story on is story (the embers carry their own ritual step).
            // That goes for switching its own parts on and off too: the merchant's wheel switches its axle's collider off
            // while carried, and with that call silenced on a guest the axle pushed the guest along for as long as they
            // held the wheel (first Steam session).
            var part = comp == null ? target.TryCast<GameObject>() : null;
            bool local = comp != null ? AreaSync.IsLocalTarget(type) || (comp.transform.IsChildOf(own) && !StoryTypes.Contains(type))
                                      : part != null && part.transform.IsChildOf(own);
            (local ? s.Local : s.Story).Add((ev, i));
        }
    }

    private static Split Of(CarriableObject co)
    {
        int id = co.GetInstanceID();
        if (Known.TryGetValue(id, out var s)) return s;
        if (Known.Count > 400) Known.Clear();
        s = new Split();
        try { Sort(s, co.transform, co.OnPickUp); Sort(s, co.transform, co.OnFirstPickUp); Sort(s, co.transform, co.OnDrop); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: can't read {co.name}'s events: {e.Message}"); }
        return Known[id] = s;
    }

    // A spot something carried is put into (the ritual's oven takes the egg): what follows from it is story.
    private static Split Of(CarriableObjectPlacementHelper h)
    {
        int id = h.GetInstanceID();
        if (Known.TryGetValue(id, out var s)) return s;
        s = new Split();
        try { Sort(s, h.transform, h.onPlace); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: can't read {h.name}'s events: {e.Message}"); }
        return Known[id] = s;
    }

    // Host: a guest put what it carried into this spot.
    internal static void PlacedFor(CarriableObjectPlacementHelper h, int player)
    {
        if (h == null) return;
        var s = Of(h);
        DialogueSync.NoteRequest(player, 10f, act: true);
        Switch(s.Local, false);
        try
        {
            h.onPlace?.Invoke();
            // The spot's own interaction usually marks it filled right after placing (the oven's condition, which the
            // ritual waits on); the guest's interaction ran on its copy only.
            var filled = h.GetComponent<GenericPOICondition>();
            if (filled != null) filled.SetConditionTrue();
            Plugin.Logger.LogInfo($"Carry: ran {h.name}'s story for player {player} putting something in it ({s.Story.Count} story calls, {s.Local.Count} local)");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: {h.name}'s story failed: {e.Message}"); }
        finally { Switch(s.Local, true); }
    }

    // Host: a guest put items from its pack into this place (an offering into a ritual bowl). The pack is shared and the
    // guest's copy already took the items, so the host's copy only becomes placed, with all that follows from it.
    internal static void ItemPlacedFor(PlaceableObject p, int player)
    {
        if (p == null || p.alreadyPlaced) return;
        DialogueSync.NoteRequest(player, 10f, act: true);
        try
        {
            var before = InventorySync.Capture()?.Items;
            p.PlaceItem();
            // Placing takes the items from this copy's pack as well; the guest's copy already paid.
            int back = 0;
            var after = InventorySync.Capture()?.Items;
            var m = Object.FindFirstObjectByType<InventoryManager>();
            if (before != null && after != null && m?.pRef != null)
                foreach (var (id, had) in before)
                {
                    int gone = had - after.GetValueOrDefault(id);
                    var item = gone > 0 ? m.pRef.IDToType(id)?.TryCast<Item>() : null;
                    if (item == null) continue;
                    m.AddItemsToPlayerInventory(item, gone);
                    back += gone;
                }
            Plugin.Logger.LogInfo($"Carry: {NameTags.NameOf(player)} put items into {p.name}; placed here too ({back} items given back to the shared pack)");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Carry: placing into {p.name} failed: {e.Message}"); }
    }

    // Guest: told before the place's own calls run, so the host's copy starts what follows (a talk) before this copy
    // asks for it.
    [HarmonyPatch(typeof(PlaceableObject), nameof(PlaceableObject.PlaceItem))]
    private static class ItemHooks
    {
        private static void Prefix(PlaceableObject __instance)
        {
            if (WorldSync.IsGuest && !WorldObjects.ApplyingRemote && !__instance.alreadyPlaced) Intents.Send(Intents.Kind.Place, __instance);
        }
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
        DialogueSync.NoteRequest(player, 10f, act: true);
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

    [HarmonyPatch(typeof(CarriableObjectPlacementHelper), nameof(CarriableObjectPlacementHelper.PlaceCarriedObject))]
    private static class PlaceHooks
    {
        private static void Prefix(CarriableObjectPlacementHelper __instance, out CarriableObject __state)
        {
            __state = WorldSync.IsGuest ? CarrySync.Mine : null;
            if (__state == null) return;
            var s = Of(__instance);
            if (!s.Silenced && s.Story.Count > 0) { s.Silenced = true; Switch(s.Story, false); }
        }

        private static void Postfix(CarriableObjectPlacementHelper __instance, CarriableObject __state)
        {
            if (__state == null || CarrySync.Mine != null) return;   // wasn't carrying, or still is: nothing was placed
            Intents.Send(Intents.Kind.Place, __instance);
        }
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
