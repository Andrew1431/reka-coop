using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RekaCoop.Sync;

// While someone else steers a conversation, this player's continue, skip and pick buttons read as not pressed. The
// dialogue box reads them itself (not only through its continue routine), so a non-starter's press would otherwise
// move or end the line on their copy.
[HarmonyPatch]
internal static class TalkInputBlock
{
    private static readonly string[] Blocked = { "AdvanceDialogue", "UISubmit", "UIAltSubmit", "Interact", "Action1" };
    private static HashSet<int> _ids;

    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(Rewired.Player).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("GetButton") && m.ReturnType == typeof(bool) && !m.Name.Contains("Prev")
                && m.GetParameters() is { Length: >= 1 } p && (p[0].ParameterType == typeof(int) || p[0].ParameterType == typeof(string)));

    private static void Postfix(object __0, ref bool __result)
    {
        if (!__result || !(DialogueSync.WatchingTalk || HutSync.WatchingDrive)) return;
        if (__0 is string name ? System.Array.IndexOf(Blocked, name) >= 0 : __0 is int id && Ids().Contains(id)) __result = false;
    }

    private static HashSet<int> Ids()
    {
        if (_ids != null) return _ids;
        var ids = new HashSet<int>();
        foreach (var n in Blocked)
        {
            var a = Rewired.ReInput.mapping?.GetAction(n);
            if (a != null) ids.Add(a.id);
        }
        if (ids.Count > 0) _ids = ids;
        return ids;
    }
}
