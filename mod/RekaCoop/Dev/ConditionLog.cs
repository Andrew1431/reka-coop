#if DEVBUILD
using HarmonyLib;

namespace RekaCoop.Dev;

// Logs every place condition the game sets, with the object path, to find what drives a quest step.
[HarmonyPatch(typeof(GenericPOICondition))]
internal static class ConditionLog
{
    [HarmonyPrefix, HarmonyPatch(nameof(GenericPOICondition.SetConditionTrue))]
    private static void True(GenericPOICondition __instance) => Log(__instance, "true");

    [HarmonyPrefix, HarmonyPatch(nameof(GenericPOICondition.SetConditionFalse))]
    private static void False(GenericPOICondition __instance) => Log(__instance, "false");

    private static void Log(GenericPOICondition c, string value)
    {
        if (c == null) return;
        Plugin.Logger.LogInfo($"[cond] {Sync.ScenePath.Of(c.transform)} -> {value} (true called {c.TrueHasBeenCalled}, false called {c.FalseHasBeenCalled})");
    }
}
#endif
