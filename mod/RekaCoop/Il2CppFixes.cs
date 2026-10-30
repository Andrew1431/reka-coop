using System;
using System.Reflection;
using HarmonyLib;

namespace RekaCoop;

// Game build 26298851: Il2CppInterop's byte signature for Class::GetDefaultFieldValue first matches an unrelated game
// function, which then gets detoured, and the game dies in coreclr shortly after Steam starts (as soon as anything
// injects a type: our driver, or BepInEx's Unity log listener). Its own fallback, walking calls from the exported
// il2cpp_field_static_get_value, finds the real one. Must run before the first ClassInjector use.
internal static class Il2CppFixes
{
    public static void Apply(Harmony harmony)
    {
        var hook = AccessTools.TypeByName("Il2CppInterop.Runtime.Injection.Hooks.Class_GetFieldDefaultValue_Hook");
        var find = hook == null ? null : AccessTools.Method(hook, "FindTargetMethod");
        if (find == null || AccessTools.Method(hook, "FindClassGetFieldDefaultValueXref") == null)
        {
            Plugin.Logger.LogWarning("Il2CppFixes: Class::GetDefaultFieldValue hook not found; left as is");
            return;
        }
        harmony.Patch(find, prefix: new HarmonyMethod(typeof(Il2CppFixes), nameof(FindByTraversal)));
    }

    private static bool FindByTraversal(object __instance, ref IntPtr __result)
    {
        try
        {
            var xref = AccessTools.Method(__instance.GetType(), "FindClassGetFieldDefaultValueXref");
            __result = (IntPtr)(nint)xref.Invoke(null, new object[] { false });
            Plugin.Logger.LogInfo($"Il2CppFixes: Class::GetDefaultFieldValue by traversal at 0x{__result.ToInt64():X}");
            return __result == IntPtr.Zero;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Il2CppFixes: traversal failed ({(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}); using the signature");
            return true;
        }
    }
}
