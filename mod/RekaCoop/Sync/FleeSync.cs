using HarmonyLib;
using UnityEngine;

namespace RekaCoop.Sync;

// Host: animals flee from guests too. Their flee triggers (ObjectDetector, tag "Player", polling path) are ticked
// with the host player's position only; for those, the closest player is put in instead, and when that is a guest's
// puppet the animal is given the puppet as the thing to run from. Guests' copies follow the host's as mirrors.
[HarmonyPatch]
internal static class FleeSync
{
    private static Transform _puppet;  // set while a detector is ticked for a guest
    internal static int Startled, Ticks, Closer;  // dev

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.ProximityTick))]
    private static void Tick(ObjectDetector __instance, ref Vector3 playerPos)
    {
        _puppet = null;
        Ticks++;
        // Only animals' flee triggers: other player detectors start story and area events, which stay the host's.
        if (CoopDriver.Session is not { Role: Net.Role.Host } || !PetEligibility.IsPlayerFleeTrigger(__instance)) return;
        var at = __instance.transform.position;
        var puppet = PlayerSync.NearestPuppetTransform(at);
        if (puppet == null || (puppet.position - at).sqrMagnitude >= (playerPos - at).sqrMagnitude) return;
        playerPos = puppet.position;
        Closer++;
        _puppet = puppet;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.ProximityTick))]
    private static void Ticked() => _puppet = null;

    [HarmonyPrefix, HarmonyPatch(typeof(GeneralAIController), nameof(GeneralAIController.GetTargetFromVariousScripts))]
    private static void Target(ref GameObject other)
    {
        if (_puppet == null) return;
        other = _puppet.gameObject;
        Startled++;
    }
}
