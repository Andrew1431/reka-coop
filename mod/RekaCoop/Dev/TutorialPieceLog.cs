#if DEVBUILD
using HarmonyLib;

namespace RekaCoop.Dev;

// The old woman's tutorial talks are pieces that an event "sets" (she then waits for the interact key) or runs at
// once. Logs each step of each piece, to see why one runs on one copy and not on the other.
[HarmonyPatch(typeof(TutorialNPCLogic.TutorialDialoguePiece))]
internal static class TutorialPieceLog
{
    private static void Log(string what, TutorialNPCLogic.TutorialDialoguePiece p)
    {
        try { Plugin.Logger.LogInfo($"[piece] {what} {p.LinesTerm} wasSet={p.wasSet} didRun={p.didRun} invalid={p.isInvalid} immediate={p.runImmediatlyInsteadOfSetting}"); }
        catch (System.Exception e) { Plugin.Logger.LogInfo($"[piece] {what} ({e.Message})"); }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(TutorialNPCLogic.TutorialDialoguePiece.SetDialogue))]
    private static void Set(TutorialNPCLogic.TutorialDialoguePiece __instance) => Log("SetDialogue", __instance);
    [HarmonyPrefix, HarmonyPatch(nameof(TutorialNPCLogic.TutorialDialoguePiece.RunDialogue))]
    private static void Run(TutorialNPCLogic.TutorialDialoguePiece __instance) => Log("RunDialogue", __instance);
    [HarmonyPrefix, HarmonyPatch(nameof(TutorialNPCLogic.TutorialDialoguePiece.OnInteract))]
    private static void Interact(TutorialNPCLogic.TutorialDialoguePiece __instance) => Log("OnInteract", __instance);
    [HarmonyPrefix, HarmonyPatch(nameof(TutorialNPCLogic.TutorialDialoguePiece.OnFinish))]
    private static void Finish(TutorialNPCLogic.TutorialDialoguePiece __instance) => Log("OnFinish", __instance);

    public static string Describe()
    {
        var n = UnityEngine.Object.FindFirstObjectByType<TutorialNPCLogic>();
        if (n == null) return "no tutorial npc";
        var sb = new System.Text.StringBuilder($"location={n.location} current={(TutorialNPCLogic.currentlySetTDP != null ? TutorialNPCLogic.currentlySetTDP.LinesTerm : "none")}");
        var all = n.allDialogue;
        for (int i = 0; all != null && i < all.Count; i++)
            sb.Append($"\n  {i}: {all[i].LinesTerm} wasSet={all[i].wasSet} didRun={all[i].didRun} invalid={all[i].isInvalid} immediate={all[i].runImmediatlyInsteadOfSetting}");
        return sb.ToString();
    }
}
#endif
