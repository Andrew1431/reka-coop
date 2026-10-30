#if DEVBUILD
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RekaCoop.Dev;

// Logs every dialogue the game starts, and the calls on the game's talk components just before, to find talks that
// start on a path DialogueSync doesn't hook (they then run on one copy only). The game's stack isn't readable here.
[HarmonyPatch(typeof(DialogueUILogic), nameof(DialogueUILogic.RequestStartDialogue))]
internal static class DialogueStartLog
{
    internal static readonly Queue<string> Recent = new();

    private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray __0, bool __5)
    {
        string first = __0 != null && __0.Length > 0 ? __0[0] : "";
        Plugin.Logger.LogInfo($"[dlgstart] passive={__5} \"{first}\" after {string.Join(" > ", Recent)}");
        Recent.Clear();
    }
}

[HarmonyPatch]
internal static class TalkCallLog
{
    private static readonly string[] Types = { "NPC_DialogueManager", "TutorialNPCLogic", "DialogueFunctions", "StandaloneDialogue", "POI_Dialogue", "BabaJagaEncounter", "BabaYagaStartTalking" };
    private static readonly string[] Prefixes = { "Say", "Start", "Trigger", "Initialize", "Present", "Talk", "Continue", "Begin", "Set" };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var asm = typeof(DialogueUILogic).Assembly;
        foreach (var tn in Types)
        {
            var t = asm.GetType(tn);
            if (t == null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (!m.IsSpecialName && !m.ContainsGenericParameters && Prefixes.Any(p => m.Name.StartsWith(p))) yield return m;
        }
    }

    private static void Prefix(object __instance, MethodBase __originalMethod)
    {
        var name = __instance is UnityEngine.Component c && c != null ? c.name : "?";
        var q = DialogueStartLog.Recent;
        q.Enqueue($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({name})");
        while (q.Count > 6) q.Dequeue();
    }
}
#endif
