using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RekaCoop.Sync;

// Spots a tool is used on (UseItemInteract: the ritual's rings swept with the pine branch, things lit, watered or
// revealed). A guest uses its own tool on its own copy and sees it work there; when a spot is done, that goes to the
// host as an intent, and the host finishes its own copy of the spot for that guest, so what follows from it (the ring
// complete, the ritual moving on) runs on the host like any story (PLAN.md, rule 2).
[HarmonyPatch]
internal static class UseItemSync
{
    private static (Component at, int index) Address(UseItemInteract u)
    {
        var area = u.GetComponentInParent<UseItemInteractArea>(true);
        if (area != null && area.interacts != null)
            for (int i = 0; i < area.interacts.Count; i++)
                if (area.interacts[i] != null && area.interacts[i].Pointer == u.Pointer) return (area, i);
        return (u, -1);
    }

    // Guest: this copy's player finished this spot.
    private static void Tell(UseItemInteract u)
    {
        var (at, index) = Address(u);
        Intents.Send(Intents.Kind.UseItem, at, args: w => w.Put(index));
    }

    // Host: a guest finished spot `index` of the area at `t` (or the spot at `t` itself when index is -1).
    internal static void Run(Transform t, int index, int player)
    {
        UseItemInteract u = null;
        if (index < 0) u = t.GetComponent<UseItemInteract>();
        else if (t.GetComponent<UseItemInteractArea>() is { } area && area.interacts != null && index < area.interacts.Count) u = area.interacts[index];
        if (u == null) { Plugin.Logger.LogWarning($"UseItem: {NameTags.NameOf(player)} finished spot {index} at {ScenePath.Of(t)}, not found here"); return; }
        if (u.solved) return;
        DialogueSync.NoteRequest(player, 10f, act: true);
        SolveFor(u, player);
        Plugin.Logger.LogInfo($"UseItem: {NameTags.NameOf(player)} finished spot {index} at {ScenePath.Of(t)}; {(u.solved ? "done here too" : "NOT done here")}");
    }

    // The spot's own way to completion, with this copy's tool of the kind it wants standing in for the guest's.
    internal static void SolveFor(UseItemInteract u, int player)
    {
        if (u == null || u.solved) return;
        var tool = UseItemManager.Instance?.allItems?.ToArray().FirstOrDefault(i => i != null && i.Type == u.interactItem);
        try
        {
            if (u.TryCast<UseItemInteract_OverTime>() is { } o)
            {
                o.Interact(tool);
                o.interacting = true;
                o.timeInteractedSoFar = o.timeToInteract;
                o.Update();
                if (tool != null) o.StopInteraction(tool);
                // The spot only fills while this copy's own player holds the tool on it; the guest did that on theirs.
                if (!o.solved)
                {
                    o.timeInteractedSoFar = o.timeToInteract;
                    o.interacting = false;
                    o.solved = true;
                    o.ShowProgress(1f);
                    o.onInteractionCompleted?.Invoke();
                    if (o.setObjectInactiveOnSolve) o.gameObject.SetActive(false);
                }
            }
            else u.Interact(tool);
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"UseItem: finishing {u.name} failed: {e.Message}"); }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UseItemInteract_OverTime), nameof(UseItemInteract_OverTime.Update))]
    private static void Before(UseItemInteract_OverTime __instance, out bool __state) => __state = __instance.solved;

    [HarmonyPostfix, HarmonyPatch(typeof(UseItemInteract_OverTime), nameof(UseItemInteract_OverTime.Update))]
    private static void After(UseItemInteract_OverTime __instance, bool __state)
    {
        if (!__state && __instance.solved && WorldSync.IsGuest && !WorldObjects.ApplyingRemote) Tell(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UseItemInteract), nameof(UseItemInteract.Interact))]
    private static void BeforeUse(UseItemInteract __instance, out bool __state) => __state = __instance.solved;

    [HarmonyPostfix, HarmonyPatch(typeof(UseItemInteract), nameof(UseItemInteract.Interact))]
    private static void AfterUse(UseItemInteract __instance, bool __state)
    {
        if (!__state && __instance.solved && WorldSync.IsGuest && !WorldObjects.ApplyingRemote) Tell(__instance);
    }
}
