using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RekaCoop.Sync;

// The ritual's bones, dissolved by walking past them with the Svarog charm in hand (BoneDissolver). The dissolver
// looks every frame for bones near the local player's charm, so each copy would dissolve its own and each would count
// them. Host-first: a guest's dissolver still looks (only it knows where its player holds the charm), but a bone it
// would start goes to the host as an intent instead, and the host starts its own copy's bone. The bones' state comes
// back with the place's state like any other, and the count with the host's tracker.
[HarmonyPatch(typeof(BoneDissolver))]
internal static class BoneSync
{
    private const float AskAgain = 4f;
    private static readonly Dictionary<long, float> Asked = new();   // dissolver and bone -> when asked

    [HarmonyPrefix, HarmonyPatch(nameof(BoneDissolver.StartDissolve))]
    private static bool Start(BoneDissolver __instance, BoneDissolver.Bone __0)
    {
        if (!WorldSync.IsGuest || WorldObjects.ApplyingRemote || __0 == null) return true;
        var bones = __instance.bones;
        int index = -1;
        if (bones != null)
            for (int i = 0; i < bones.Count; i++)
                if (bones[i] != null && bones[i].Pointer == __0.Pointer) { index = i; break; }
        if (index < 0) return false;
        long key = ((long)__instance.GetInstanceID() << 16) ^ (uint)index;
        if (Asked.TryGetValue(key, out float at) && Time.unscaledTime - at < AskAgain) return false;
        if (Asked.Count > 500) Asked.Clear();
        Asked[key] = Time.unscaledTime;
        Intents.Send(Intents.Kind.UseItem, __instance, args: w => w.Put(index));
        return false;
    }

    // What happens at so many bones gone (the old woman's remarks, the whirlwind) is the host's to start.
    [HarmonyPrefix, HarmonyPatch(nameof(BoneDissolver.InvokeEvents))]
    private static bool Events() => !WorldSync.IsGuest;

    // Host: a guest's charm reached this bone.
    internal static void StartFor(BoneDissolver d, int index, int player)
    {
        var bones = d.bones;
        if (bones == null || index < 0 || index >= bones.Count || bones[index] == null) return;
        try { d.StartDissolve(bones[index]); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Bones: starting bone {index} for {NameTags.NameOf(player)} failed: {e.Message}"); }
    }
}
