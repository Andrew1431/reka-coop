using System.Collections.Generic;
using UnityEngine;

namespace RekaCoop.Sync;

// A puppet's copies of the remote player's gear. Each copy is cloned from this machine's own gear object with the
// same key, stripped to meshes and lights, and hung on the puppet's matching bone or backpack slot.
public class PuppetGear
{
    private static readonly HashSet<string> Keep = new()
    {
        "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup", "Light", "HDAdditionalLightData",
        "VisualEffect", "VFXRenderer",  // the lantern's flame and glow are effects; its Light alone is only a few lumen
    };

    private class Copy
    {
        public GameObject Root;
        public List<Transform> Toggles;
        public List<Light> Lights;  // found before the followers move onto bones
        public List<UnityEngine.VFX.VisualEffect> Effects;
        public byte Playing;
        public string Anchor, Follower;
        public Transform FollowerT;
        public readonly Dictionary<string, Transform> Followers = new();  // by path in the copy; they get moved onto bones
    }

    private readonly Puppet _puppet;
    private readonly Dictionary<string, Copy> _copies = new();
    private Dictionary<string, GameObject> _sources;

    public PuppetGear(Puppet puppet) => _puppet = puppet;

    public void Apply(GearState state, PlayerMovement_2021 local)
    {
        _sources ??= GearObjects.Find(local);
        foreach (var e in state.Entries)
        {
            if (!_copies.TryGetValue(e.Key, out var copy))
            {
                if (!e.Active) continue;  // never shown yet: don't bother cloning
                copy = MakeCopy(e.Key);
                if (copy == null) continue;
                _copies[e.Key] = copy;
            }
            if (!e.Active)
            {
                copy.Root.SetActive(false);
                continue;
            }
            if (!Place(copy, e)) { copy.Root.SetActive(false); continue; }
            for (int i = 0; i < copy.Toggles.Count; i++)
                if (copy.Toggles[i] != null) copy.Toggles[i].gameObject.SetActive((e.Children & (1UL << i)) != 0);
            // Brightness too: the lantern shines brighter while its button is held.
            for (int i = 0; i < copy.Lights.Count && 2 * i + 1 < e.Lights.Length; i++)
            {
                var l = copy.Lights[i];
                if (l == null) continue;
                l.enabled = e.Lights[2 * i] > 0;
                l.intensity = e.Lights[2 * i];
                l.range = e.Lights[2 * i + 1];
            }
            copy.Root.SetActive(true);
            for (int i = 0; i < copy.Effects.Count; i++)
            {
                var v = copy.Effects[i];
                bool want = (e.Effects & (1 << i)) != 0, had = (copy.Playing & (1 << i)) != 0;
                if (v == null || want == had) continue;
                if (want) v.Play(); else v.Stop();
                Plugin.Logger.LogInfo($"Puppet {_puppet.PlayerId}: {(want ? "played" : "stopped")} {v.name} on {e.Key}");
            }
            copy.Playing = e.Effects;
        }
    }

    private Copy MakeCopy(string key)
    {
        if (!_sources.TryGetValue(key, out var source) || source == null)
        {
            Plugin.Logger.LogWarning($"Puppet {_puppet.PlayerId}: no local gear '{key}' to copy");
            return null;
        }
        var holder = new GameObject("RekaCoop_GearHolder");
        holder.SetActive(false);
        var clone = Object.Instantiate(source, holder.transform);
        clone.name = "RekaCoopGear_" + source.name;  // not "(Clone)": Puppet.ApplyLook removes those
        var toggles = GearObjects.Toggles(clone.transform);
        var lights = GearObjects.LightsOf(clone.transform);
        var effects = GearObjects.EffectsOf(clone.transform);
        Strip(clone);
        foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
        clone.transform.SetParent(_puppet.Root.transform, false);
        Object.Destroy(holder);
        Plugin.Logger.LogInfo($"Puppet {_puppet.PlayerId}: copied gear {key}");
        return new Copy { Root = clone, Toggles = toggles, Lights = lights, Effects = effects };
    }

    private bool Place(Copy copy, GearState.Entry e)
    {
        if (copy.Anchor != e.Anchor || copy.Follower != e.Follower || copy.FollowerT == null)
        {
            var anchor = ResolveAnchor(e.Anchor);
            if (anchor == null) return false;
            if (!copy.Followers.TryGetValue(e.Follower, out var follower) || follower == null)
            {
                follower = e.Follower.Length == 0 ? copy.Root.transform : copy.Root.transform.Find(e.Follower);
                if (follower == null) return false;
                copy.Followers[e.Follower] = follower;
            }
            // The following transform moves onto the bone; a skinned gear mesh keeps rendering from its bones.
            follower.SetParent(anchor, false);
            copy.Anchor = e.Anchor;
            copy.Follower = e.Follower;
            copy.FollowerT = follower;
        }
        var t = copy.FollowerT;
        t.localPosition = e.Pos;
        t.localRotation = e.Rot;
        t.localScale = e.Scale;
        return true;
    }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, c) in _copies)
        {
            var f = c.FollowerT ?? c.Root.transform;
            int shown = 0, all = 0;
            foreach (var r in c.Root.GetComponentsInChildren<Renderer>(true)) { all++; if (r.enabled && r.gameObject.activeInHierarchy) shown++; }
            sb.Append($"\n  {key}: active={c.Root.activeSelf} anchor={c.Anchor} pos={f.position} d={Vector3.Distance(f.position, _puppet.Root.transform.position):0.00} scale={f.lossyScale} renderers {shown}/{all}");
            foreach (var l in c.Lights) if (l != null) sb.Append($" light[{l.enabled}/{l.gameObject.activeInHierarchy} {l.intensity:0.##} r{l.range:0.#} {l.lightUnit}]");
            sb.Append($" effects {c.Effects.Count} playing={c.Playing}");
        }
        return sb.ToString();
    }

    private Transform ResolveAnchor(string anchor)
    {
        int slash = anchor.IndexOf('/');
        string head = slash < 0 ? anchor : anchor.Substring(0, slash);
        string rest = slash < 0 ? "" : anchor.Substring(slash + 1);
        Transform root = head switch
        {
            "Reka_Body_Base_01" => _puppet.Root.transform,
            "Backpack" => BackpackRoot(),
            _ => null,
        };
        if (root == null) return null;
        return rest.Length == 0 ? root : root.Find(rest);
    }

    private Transform BackpackRoot()
    {
        foreach (var (key, copy) in _copies)
            if (key.EndsWith("/Backpack")) return copy.FollowerT ?? copy.Root.transform;
        return null;
    }

    private static void Strip(GameObject go)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool removed = false;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || Keep.Contains(c.GetIl2CppType().Name)) continue;
                Object.DestroyImmediate(c);
                removed = true;
            }
            if (!removed) break;
        }
    }
}
