using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace RekaCoop.Sync;

// What a player is carrying: backpack, held items, tools, lantern and its light. None of these are parented to the
// body; each follows a bone (or a backpack slot) through the game's ObjectFollower. We send, per gear object,
// whether it is shown, which bone it follows (path from PlayerCameraSetup) and its pose relative to that bone.
public class GearState
{
    public class Entry
    {
        public string Key;          // path of the gear object; the same objects exist on every machine
        public bool Active;
        public string Anchor = "";  // "Reka_Body_Base_01/root/..." or "Backpack/ToolSlot_..."
        public string Follower = ""; // path from the gear object to the transform that follows the anchor
        public Vector3 Pos, Scale = Vector3.one;
        public Quaternion Rot = Quaternion.identity;
        public ulong Children;      // active flags of the gear's renderer/light children (see GearObjects.Toggles)
        public float[] Lights = System.Array.Empty<float>();  // per Light in the gear (GearObjects.LightsOf order): intensity (0 = off), range
        public byte Effects;        // per effect in the gear (GearObjects.EffectsOf order): playing (the raised lantern's glow)
    }

    public readonly List<Entry> Entries = new();

    public void Write(NetDataWriter w)
    {
        w.Put((byte)Entries.Count);
        foreach (var e in Entries)
        {
            w.Put(e.Key);
            w.Put(e.Active);
            if (!e.Active) continue;
            w.Put(e.Anchor);
            w.Put(e.Follower);
            w.Put(e.Pos.x); w.Put(e.Pos.y); w.Put(e.Pos.z);
            w.Put(e.Rot.x); w.Put(e.Rot.y); w.Put(e.Rot.z); w.Put(e.Rot.w);
            w.Put(e.Scale.x); w.Put(e.Scale.y); w.Put(e.Scale.z);
            w.Put(e.Children);
            w.Put((byte)(e.Lights.Length / 2));
            foreach (var f in e.Lights) w.Put(f);
            w.Put(e.Effects);
        }
    }

    public static GearState Read(NetDataReader r)
    {
        var s = new GearState();
        int n = r.GetByte();
        for (int i = 0; i < n; i++)
        {
            var e = new Entry { Key = r.GetString(), Active = r.GetBool() };
            if (e.Active)
            {
                e.Anchor = r.GetString();
                e.Follower = r.GetString();
                e.Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                e.Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                e.Scale = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                e.Children = r.GetULong();
                e.Lights = new float[r.GetByte() * 2];
                for (int k = 0; k < e.Lights.Length; k++) e.Lights[k] = r.GetFloat();
                e.Effects = r.GetByte();
            }
            s.Entries.Add(e);
        }
        return s;
    }
}

// Finds the local player's gear objects by key, and reads their current state.
public static class GearObjects
{
    public const string SetupName = "PlayerCameraSetup";

    // Every gear object of the local player, keyed by path. Backpack comes first: other gear can hang in its slots.
    public static Dictionary<string, GameObject> Find(PlayerMovement_2021 local)
    {
        var map = new Dictionary<string, GameObject>();
        var setup = local.transform.parent;
        if (setup == null) return map;
        void Add(GameObject go) { if (go != null) map.TryAdd(PathOf(go.transform), go); }

        Add(setup.Find("Backpack")?.gameObject);
        Add(setup.Find("LanternHolder")?.gameObject);
        Add(setup.Find("ItemsInRightHand")?.gameObject);
        var tools = setup.Find("Tools");
        if (tools != null) for (int i = 0; i < tools.childCount; i++) Add(tools.GetChild(i).gameObject);

        var uim = UseItemManager.Instance;
        if (uim?.allItems != null)
        {
            foreach (var item in uim.allItems)
            {
                if (item == null) continue;
                Add(item.physicalTool);
                if (item.backpackTool != item.physicalTool) Add(item.backpackTool);
            }
        }
        return map;
    }

    // Children whose on/off state is worth sending (lights, meshes), in a fixed depth-first order.
    public static List<Transform> Toggles(Transform gear)
    {
        var list = new List<Transform>();
        void Walk(Transform t)
        {
            for (int i = 0; i < t.childCount && list.Count < 64; i++)
            {
                var c = t.GetChild(i);
                if (c.GetComponent<Renderer>() != null || c.GetComponent<Light>() != null) list.Add(c);
                Walk(c);
            }
        }
        Walk(gear);
        return list;
    }

    // The gear's effects in a fixed order. Raising the lantern plays its glow (vfx_LanternBright); the copy on a puppet
    // never played it.
    public static List<UnityEngine.VFX.VisualEffect> EffectsOf(Transform gear)
    {
        var list = new List<UnityEngine.VFX.VisualEffect>();
        foreach (var v in gear.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true)) { list.Add(v); if (list.Count == 8) break; }
        return list;
    }

    // The gear's lights in a fixed order (both machines have the same gear objects).
    public static List<Light> LightsOf(Transform gear)
    {
        var list = new List<Light>();
        foreach (var l in gear.GetComponentsInChildren<Light>(true)) { list.Add(l); if (list.Count == 8) break; }
        return list;
    }

    public static GearState.Entry Capture(string key, GameObject go)
    {
        var e = new GearState.Entry { Key = key, Active = go.activeInHierarchy };
        if (!e.Active) return e;

        // The transform that actually follows something: the gear object itself or a bone inside it. Some have two
        // followers on at once (the lantern's bone: backpack hook and hand); the one it sits at is the one in charge.
        Transform follower = go.transform, target = null;
        float best = float.MaxValue;
        foreach (var f in go.GetComponentsInChildren<ObjectFollower>(false))
        {
            if (!f.enabled) continue;
            var t = f.followTargetFromTransformReference ? f.followTargetTransformReference?.transform : f.followTarget?.transform;
            if (t == null || AnchorOf(t) == null) continue;
            float d = (f.transform.position - t.position).sqrMagnitude;
            if (d >= best) continue;
            best = d;
            follower = f.transform;
            target = t;
        }
        if (target == null) target = RekaCoop.Sync.Find.One<PlayerMovement_2021>()?.transform;  // no follower: body-relative
        if (target == null) { e.Active = false; return e; }
        // Unused gear is parked far away instead of being switched off. A lit light is in use though: the lantern's
        // night light (LanternHolder) stays wherever it was last switched off and is lit before it catches up.
        var lights = LightsOf(go.transform);
        e.Lights = new float[lights.Count * 2];
        bool lit = false;
        for (int i = 0; i < lights.Count; i++)
        {
            var l = lights[i];
            bool on = l.enabled && l.gameObject.activeInHierarchy;
            lit |= on && l.intensity > 0;
            e.Lights[2 * i] = on ? l.intensity : 0;
            e.Lights[2 * i + 1] = l.range;
        }
        var effects = EffectsOf(go.transform);
        for (int i = 0; i < effects.Count; i++)
            if (effects[i] != null && effects[i].isActiveAndEnabled && effects[i].HasAnySystemAwake()) e.Effects |= (byte)(1 << i);
        if ((follower.position - target.position).sqrMagnitude > 25f)
        {
            if (!lit) { e.Active = false; return e; }
            e.Anchor = AnchorOf(target);
            e.Follower = RelativePath(go.transform, follower);
            e.Pos = Vector3.zero;
            e.Rot = Quaternion.identity;
            return e;
        }

        e.Anchor = AnchorOf(target);
        e.Follower = RelativePath(go.transform, follower);
        e.Pos = target.InverseTransformPoint(follower.position);
        e.Rot = Quaternion.Inverse(target.rotation) * follower.rotation;
        e.Scale = Div(follower.lossyScale, target.lossyScale);

        var toggles = Toggles(go.transform);
        for (int i = 0; i < toggles.Count; i++)
            if (toggles[i].gameObject.activeSelf) e.Children |= 1UL << i;
        return e;
    }

    // Path below PlayerCameraSetup, only for anchors a puppet has: its body and its backpack.
    private static string AnchorOf(Transform t)
    {
        string path = null;
        for (var p = t; p != null; p = p.parent)
        {
            if (p.parent != null && p.parent.name == SetupName && (p.name == "Reka_Body_Base_01" || p.name == "Backpack"))
                return path == null ? p.name : p.name + "/" + path;
            path = path == null ? p.name : p.name + "/" + path;
        }
        return null;
    }

    public static string RelativePath(Transform root, Transform t)
    {
        string path = "";
        for (var p = t; p != null && p != root; p = p.parent) path = path.Length == 0 ? p.name : p.name + "/" + path;
        return path;
    }

    public static string PathOf(Transform t)
    {
        var s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    public static Vector3 Div(Vector3 a, Vector3 b) =>
        new(b.x != 0 ? a.x / b.x : 1, b.y != 0 ? a.y / b.y : 1, b.z != 0 ? a.z / b.z : 1);
}
