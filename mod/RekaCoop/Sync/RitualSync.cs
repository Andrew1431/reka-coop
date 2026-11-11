using System.Collections.Generic;
using HarmonyLib;

namespace RekaCoop.Sync;

// The ritual moves on through RitualProgressionAdvancer.Advance, called by whatever in the scene noticed the step (a
// talk ending, the chicken sitting in the nest, a site cleansed). Host-first: only the host's copy advances. A guest's
// copy reaches the same calls from the mirrored world and from its own player's acts, which the host hears about as
// intents and runs itself; if the guest's copy advanced too, every step would be counted twice.
[HarmonyPatch]
internal static class RitualSync
{
    private static readonly HashSet<int> Logged = new();

    [HarmonyPrefix, HarmonyPatch(typeof(RitualProgressionAdvancer), nameof(RitualProgressionAdvancer.Advance))]
    private static bool Advance(RitualProgressionAdvancer __instance)
    {
        if (!WorldSync.IsGuest)
        {
            if (CoopDriver.Session is { IsConnected: true }) Plugin.Logger.LogInfo($"Ritual: {__instance.step} ({__instance.name})");
            return true;
        }
        // The game's own "the ritual is over" switch is flipped inside Advance; a guest's copy would never have it.
        if (__instance.step == RitualProgressionAdvancer.RitualStep.RitualFinished) RitualIdentifier.ritualFinished = true;
        if (Logged.Add(__instance.GetInstanceID()))
            Plugin.Logger.LogInfo($"Ritual: {__instance.step} ({__instance.name}) reached here; the host's copy decides");
        return false;
    }
}
