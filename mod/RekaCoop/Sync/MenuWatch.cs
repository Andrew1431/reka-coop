using HarmonyLib;

namespace RekaCoop.Sync;

// Whether the game's main menu is up, without searching the scene for it. A search that finds nothing walks every
// object in a loaded world (a visible hitch each time, seen twice a second on the host).
[HarmonyPatch(typeof(MainMenu))]
internal static class MenuWatch
{
    internal static MainMenu Current { get; private set; }
    internal static bool Up => Current != null;

    [HarmonyPostfix, HarmonyPatch(nameof(MainMenu.Awake))]
    private static void Awake(MainMenu __instance) => Current = __instance;

    [HarmonyPrefix, HarmonyPatch(nameof(MainMenu.OnDestroy))]
    private static void OnDestroy(MainMenu __instance)
    {
        if (Current != null && Current.Pointer == __instance.Pointer) Current = null;
    }
}
