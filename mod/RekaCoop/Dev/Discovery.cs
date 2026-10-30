#if DEVBUILD
using System.Text;
using UnityEngine;

namespace RekaCoop.Dev;

// Logs, once in the world, every renderer that is skinned to the local player's bones, by full path.
internal static class Discovery
{
    private static bool _done;
    private static float _seenAt = -1;

    public static void Tick()
    {
        if (_done) return;
        var move = Object.FindFirstObjectByType<PlayerMovement_2021>();
        if (move == null) return;
        // The character's clothes and body meshes are assembled after the player spawns.
        if (_seenAt < 0) _seenAt = Time.unscaledTime;
        if (Time.unscaledTime - _seenAt < 20f) return;
        _done = true;

        var body = move.transform;
        var sb = new StringBuilder("Discovery: renderers tied to the player body\n");
        foreach (var smr in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var rootBone = smr.rootBone;
            bool ours = smr.transform.IsChildOf(body) || (rootBone != null && rootBone.IsChildOf(body));
            if (!ours)
            {
                sb.Append($"  other SMR {Path(smr.transform)}\n");
                continue;
            }
            sb.Append($"  SMR {Path(smr.transform)} rootBone={(rootBone ? Path(rootBone) : "null")} bones={smr.bones.Length} enabled={smr.enabled} active={smr.gameObject.activeInHierarchy}\n");
        }
        foreach (var mr in body.GetComponentsInChildren<MeshRenderer>(true))
            sb.Append($"  MR {Path(mr.transform)}\n");
        Plugin.Logger.LogInfo(sb.ToString());
    }

    private static string Path(Transform t)
    {
        var s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
#endif
