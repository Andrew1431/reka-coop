using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Quest rewards go to everyone through the shared pack: the story runs on the host, so rewards are handed out there
// once and the pack sync carries them to every guest. Guests don't hand out quest rewards themselves. Treasure chests
// are world loot, not rewards: first come.
public static class RewardSync
{
    // World loot (chests, loot rooms, breakable pots) is first come; only the rest is a reward.
    // Pots drop their loot from inside DestructibleObject.DamageObject, on a spawned copy outside the original.
    internal static int Breaking;

    internal static bool IsTreasure(POI_Loot loot)
    {
        if (loot == null) return false;
        if (Breaking > 0 || loot.GetComponentInParent<TreasureChest>() != null || loot.GetComponentInParent<DestructibleObject>() != null
            || loot.GetComponentInParent<Lootroom>() != null) return true;
        var name = loot.transform.root.name + "/" + loot.name;
        if (name.Contains("Destruct") || name.Contains("LootRoom") || name.Contains("Chest")) return true;
        // Loot places (a bag in a tree, a well, a loot room) sit inside bigger places like Tutorial_POIs.
        for (var t = loot.transform; t != null; t = t.parent)
            if (t.name.StartsWith("POI_Loot")) return true;
        return false;
    }

    // Dev: hand out the current trial's reward on the host.
    public static string TestTrialReward(int trial)
    {
        var tracker = RekaCoop.Sync.Find.One<TasksTracker>();
        if (tracker == null) return "no TasksTracker";
        tracker.GiveTrialReward(trial, true);
        return $"gave trial {trial} reward (current {tracker._currentTrialIndex})";
    }

    // Dev: hand out a small villager reward through a POI_Loot outside any treasure chest.
    public static string TestLootReward()
    {
        foreach (var loot in Object.FindObjectsByType<POI_Loot>(FindObjectsSortMode.None))
        {
            if (IsTreasure(loot)) continue;
            try { loot.GiveSmallLootReward(1); }
            catch (System.Exception e) { Plugin.Logger.LogInfo($"Reward test: {loot.name} can't: {e.Message}"); continue; }
            return $"small loot reward from {loot.transform.root.name}/{loot.name}";
        }
        return "no POI_Loot";
    }
}

[HarmonyPatch]
internal static class RewardHooks
{
    // Guests don't hand out rewards from their own copy of the story; the host's arrive instead.
    [HarmonyPrefix, HarmonyPatch(typeof(TasksTracker), nameof(TasksTracker.GiveTrialReward))]
    private static bool TrialBefore()
    {
        if (WorldSync.IsGuest) { Plugin.Logger.LogInfo("Reward: guest skips its own trial reward"); return false; }
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(POI_Loot), nameof(POI_Loot.GiveItemsToPlayer))]
    private static bool LootBefore(POI_Loot __instance)
    {
        // Someone else broke this pot or opened this chest; they got what was inside.
        if (WorldObjects.ApplyingRemote) { Plugin.Logger.LogInfo($"Reward: {__instance.name} was looted by someone else"); return false; }
        if (RewardSync.IsTreasure(__instance)) return true;
        if (WorldSync.IsGuest) { Plugin.Logger.LogInfo($"Reward: guest skips its own reward from {__instance.name}"); return false; }
        return true;
    }
}
