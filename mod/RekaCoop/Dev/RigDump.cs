#if DEVBUILD
using System.Linq;
using System.Text;
using UnityEngine;

namespace RekaCoop.Dev;

// `rig` dev command: logs the local player's whole rig (what gear objects exist, what they follow, animator layers)
// so puppets can mirror it.
internal static class RigDump
{
    public static string Run()
    {
        var move = Object.FindFirstObjectByType<PlayerMovement_2021>();
        if (move == null) return "no player";
        var sb = new StringBuilder("\n");
        var anim = move.playerAnimator;
        sb.Append($"animator on {Path(anim.transform)} layers={anim.layerCount}\n");
        for (int i = 0; i < anim.layerCount; i++)
            sb.Append($"  layer {i} '{anim.GetLayerName(i)}' weight={anim.GetLayerWeight(i):0.00} state={anim.GetCurrentAnimatorStateInfo(i).fullPathHash}\n");

        var root = move.transform.root;
        sb.Append($"tree of {Path(root)} (transforms with components, plus depth <= 3)\n");
        Walk(root, 0, sb);

        var uim = UseItemManager.Instance;
        if (uim != null)
        {
            sb.Append($"UseItemManager current={uim.currentItem?.ID} left={uim.inLeftHand?.ID} backpack={(uim.backpack ? Path(uim.backpack.transform) : "null")}\n");
            foreach (var item in uim.allItems)
                sb.Append($"  item {item.ID} type={item.Type} unlocked={item.unlocked} equipped={item.IsEquipped} phys={item.isPhysicalTool} slot={item.physicalToolSlot} " +
                          $"tool={(item.physicalTool ? Path(item.physicalTool.transform) + (item.physicalTool.activeInHierarchy ? " (on)" : " (off)") : "-")} " +
                          $"bpTool={(item.backpackTool ? Path(item.backpackTool.transform) : "-")}\n");
        }
        foreach (var f in Object.FindObjectsByType<ObjectFollower>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var target = f.followTargetFromTransformReference ? f.followTargetTransformReference?.transform : f.followTarget?.transform;
            sb.Append($"follower {Path(f.transform)} -> {(target ? Path(target) : "null")} enabled={f.enabled && f.gameObject.activeInHierarchy}\n");
        }
        return sb.ToString();
    }

    private static void Walk(Transform t, int depth, StringBuilder sb)
    {
        var comps = t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform").ToArray();
        if (comps.Length > 0 || depth <= 3)
            sb.Append($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " [off]")}: {string.Join(", ", comps)}\n");
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
    }

    internal static string Path(Transform t)
    {
        var s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
#endif
