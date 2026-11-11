using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RekaCoop.Sync;

// Story pickups (the tutorial's book, the pine branch, the teleport talisman) run on the host, like talks: the story is
// the host's. Used on a guest's copy they changed only that copy: the book's place was put back by PoiSync, the
// talisman stayed on the host's bench, the branch's step ran on the guest alone. A guest's use of one is sent to the
// host as if the guest had pressed the key there; what it changes (the object gone, items in the shared pack, the next
// tutorial step) comes back the usual ways. Story talks set off by using something ("Hut, hut, sit down!") go
// whole too: the same use also switches cameras and props, and the guest's copy alone used to end the ritual's
// cinematic camera (the host's stayed on, and its state then put the guest's back on). Picked out by what using them
// calls, so ordinary pickups, carrying, stations and chests stay local.
public static class StoryInteract
{
    private static readonly HashSet<string> StoryTargets = new()
    {
        "CollectibleJournal", "Poi_Tutorial_TeleportTalisman", "GivePlayerItemsHelper", "StandaloneDialogue",
        // What a generated place counts, solves and pays out with (a stuck Lutki freed: one of three, its loot, the place
        // solved, the forest spirit's favour).
        "CountUpAndDoSth_StaticallySynced", "PoiQuestInterface", "POI_Loot", "LeshyApprovalHelper", "QuestPageHelper",
    };
    private static readonly Dictionary<System.IntPtr, bool> Known = new();
    private static readonly Dictionary<System.IntPtr, float> SentAt = new();
    internal static bool Running;

    public static void ResetLocal() { Known.Clear(); SentAt.Clear(); }

    internal static bool IsStory(PlayerDetection pd)
    {
        if (Known.TryGetValue(pd.Pointer, out var yes)) return yes;
        yes = false;
        var ev = pd.OnTryToInteract;
        if (ev != null)
            for (int i = 0; i < ev.GetPersistentEventCount() && !yes; i++)
            {
                var t = ev.GetPersistentTarget(i);
                yes = t != null && StoryTargets.Contains(t.GetIl2CppType().Name);
            }
        Known[pd.Pointer] = yes;
        return yes;
    }

    // Guest: false when the host was asked to do it instead.
    internal static bool Local(PlayerDetection pd)
    {
        if (Running || !WorldSync.IsGuest || pd == null || !IsStory(pd) || DialogueSync.Personal(pd)) return true;
        // Pressing the key can come through more than one of the hooked calls.
        if (SentAt.TryGetValue(pd.Pointer, out var at) && Time.unscaledTime - at < 1f) return false;
        SentAt[pd.Pointer] = Time.unscaledTime;
        Intents.Send(Intents.Kind.Interact, pd);
        return false;
    }

    // Host, for a guest.
    internal static void Run(PlayerDetection pd)
    {
        if (pd == null) return;
        Running = true;
        try { pd.SimulateInteraction(); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Story: using {pd.name} failed: {e.Message}"); }
        finally { Running = false; }
    }
}

[HarmonyPatch(typeof(PlayerDetection))]
internal static class StoryInteractHooks
{
    [HarmonyPrefix, HarmonyPatch(nameof(PlayerDetection.TryToInteract))]
    private static bool Try(PlayerDetection __instance)
    {
        // The host pressed the key itself: whatever talk this starts is the host's, even if a guest walked through some
        // detector moments ago (seen: a guest outside the yard led the host's talk with the old woman).
        if (!StoryInteract.Running && CoopDriver.Session is { Role: Net.Role.Host, IsConnected: true }) DialogueSync.NoteRequest(DialogueSync.Me, 10f, act: true);
        return StoryInteract.Local(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(PlayerDetection.SimulateInteraction))]
    private static bool Simulate(PlayerDetection __instance)
    {
        // The same for a use that doesn't come from the key (the dev `interact` the regression suite presses with):
        // seen on the second test PC, a guest standing in a villager's detector led the talk the host started.
        if (!StoryInteract.Running && CoopDriver.Session is { Role: Net.Role.Host, IsConnected: true }) DialogueSync.NoteRequest(DialogueSync.Me, 10f, act: true);
        return StoryInteract.Local(__instance);
    }
}
