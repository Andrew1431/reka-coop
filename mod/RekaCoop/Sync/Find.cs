using System;
using System.Collections.Generic;
using UnityEngine;

namespace RekaCoop.Sync;

// Searching the scene for an object is slow in this game (about a hundred times a field read), and the sync modules
// ask for the same few objects every poll. These remember what they found until it is destroyed.
public static class Find
{
    private static readonly Dictionary<Type, UnityEngine.Object> One_ = new();
    private static readonly Dictionary<Type, float> MissedAt = new();
    private const float MissFor = 5f;
    internal static readonly Dictionary<string, int> Misses = new();  // per type, for the [perf] line

    // The first active one; a miss is remembered for a few seconds. A search that finds nothing walks the whole scene,
    // a hitch the player sees (e.g. the hut before the ritual, the ravens before they're unlocked).
    public static T One<T>() where T : UnityEngine.Object
    {
        var type = typeof(T);
        if (One_.TryGetValue(type, out var hit) && hit != null) return (T)hit;
        if (MissedAt.TryGetValue(type, out var at) && Time.unscaledTime - at < MissFor) return null;
        var found = UnityEngine.Object.FindFirstObjectByType<T>();
        if (found == null) { MissedAt[type] = Time.unscaledTime; Misses[type.Name] = Misses.GetValueOrDefault(type.Name) + 1; return null; }
        One_[type] = found;
        return found;
    }

    // A scene search for a list costs ~20 ms in one frame. Lists asked for here are instead gathered by walking the
    // loaded scenes' root objects a few at a time (under a millisecond a frame) and handed out once a pass is done:
    // null until the first pass, then the last complete list, renewed when it is older than maxAge.
    public static T[] Scanned<T>(float maxAge, bool inactive = false) where T : Component
    {
        if (!Wanted.TryGetValue(typeof(T), out var w)) Wanted[typeof(T)] = w = new Want<T>(inactive);
        w.MaxAge = Mathf.Min(w.MaxAge, maxAge);
        return (T[])w.Done;
    }

    private abstract class Want
    {
        public float MaxAge = float.MaxValue, At = float.MinValue;
        public bool Inactive, Collecting;
        public Array Done;
        public abstract void Begin();
        public abstract void Collect(GameObject root);
        public abstract void CollectOwn(GameObject go);
        public abstract void Finish();
    }

    private class Want<T> : Want where T : Component
    {
        private readonly List<T> _found = new();
        private readonly HashSet<IntPtr> _seen = new();
        public Want(bool inactive) { Inactive = inactive; }
        public override void Begin() { _found.Clear(); _seen.Clear(); Collecting = true; }
        // Filled in place: a new array per root and type was most of the game-heap garbage the mod made.
        private readonly Il2CppSystem.Collections.Generic.List<T> _buffer = new();
        public override void Collect(GameObject root)
        {
            if (!Inactive && !root.activeInHierarchy) return;
            root.GetComponentsInChildren(Inactive, _buffer);
            Take();
        }
        public override void CollectOwn(GameObject go)
        {
            if (!Inactive && !go.activeInHierarchy) return;
            go.GetComponents(_buffer);
            Take();
        }
        private void Take()
        {
            for (int i = 0; i < _buffer.Count; i++)
                if (_buffer[i] is { } c && _seen.Add(c.Pointer)) _found.Add(c);
            _buffer.Clear();
        }
        public override void Finish() { Done = _found.ToArray(); At = Time.unscaledTime; Collecting = false; }
    }

    private static readonly Dictionary<Type, Want> Wanted = new();
    private static readonly List<GameObject> Roots = new();
    private static readonly System.Diagnostics.Stopwatch Budget = new();
    private static int _root = -1;
    private const double BudgetMs = 0.7;
    private const int SplitAbove = 8;
    internal static double SlowestMs;  // dev: the costliest single search so far
    internal static string Slowest;
    private static void Note(string what, System.Diagnostics.Stopwatch sw)
    {
        if (sw.Elapsed.TotalMilliseconds > SlowestMs) { SlowestMs = sw.Elapsed.TotalMilliseconds; Slowest = what; }
    }

    // Once a frame.
    public static void Tick()
    {
        if (_root < 0)
        {
            Budget.Restart();
            bool any = false;
            foreach (var w in Wanted.Values)
                if (Time.unscaledTime - w.At >= w.MaxAge) { w.Begin(); any = true; }
            if (!any) return;
            Roots.Clear();
            _scenes = 0;
            var scenes = new HashSet<int>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                AddScene(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i), scenes);
            // DontDestroyOnLoad isn't among the loaded scenes; the hut and the player may live there.
            if (BB_Building.Home is { } home) AddScene(home.gameObject.scene, scenes);
            if (One<PlayerMovement_2021>() is { } me) AddScene(me.gameObject.scene, scenes);
            _root = 0;
            _index = 0;
            Note("(listing roots)", Budget);
            return;
        }
        Budget.Restart();
        GameObject root;
        while (Budget.Elapsed.TotalMilliseconds < BudgetMs && (root = NextRoot()) != null)
        {
            // A root with many children (a town, the trial site) is taken apart, so no single search blows the budget.
            var t = root.transform;
            if (t.childCount > SplitAbove)
            {
                foreach (var w in Wanted.Values)
                    if (w.Collecting) w.CollectOwn(root);
                for (int i = 0; i < t.childCount; i++) Roots.Add(t.GetChild(i).gameObject);
                continue;
            }
            double before = Budget.Elapsed.TotalMilliseconds;
            foreach (var w in Wanted.Values)
                if (w.Collecting) w.Collect(root);
            double took = Budget.Elapsed.TotalMilliseconds - before;
            if (took > SlowestMs) { SlowestMs = took; Slowest = $"{root.name} ({t.childCount} children)"; }
        }
        if (_root >= 0 && (_root < _scenes || Roots.Count > 0)) return;
        Budget.Restart();
        foreach (var w in Wanted.Values)
            if (w.Collecting) w.Finish();
        _root = -1;
        Note("(finishing)", Budget);
    }

    // A loaded world has over 12,000 root objects. Handing them all over as one array costs ~65 ms (each needs a
    // managed wrapper); the game's own list is filled natively instead and read one root at a time.
    private static readonly List<Il2CppSystem.Collections.Generic.List<GameObject>> SceneRoots = new();
    private static int _scenes, _index;

    private static void AddScene(UnityEngine.SceneManagement.Scene scene, HashSet<int> seen)
    {
        if (!scene.IsValid() || !scene.isLoaded || !seen.Add(scene.handle)) return;
        if (SceneRoots.Count <= _scenes) SceneRoots.Add(new Il2CppSystem.Collections.Generic.List<GameObject>());
        var list = SceneRoots[_scenes++];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        list.Clear();
        double clear = sw.Elapsed.TotalMilliseconds;
        scene.GetRootGameObjects(list);
        Note($"(roots of {scene.name}: {list.Count}, clear {clear:0.0} ms)", sw);
    }

    // Children of split roots first, then the scenes' roots in order; null when the pass is done.
    private static GameObject NextRoot()
    {
        while (true)
        {
            if (Roots.Count > 0)
            {
                var last = Roots[Roots.Count - 1];
                Roots.RemoveAt(Roots.Count - 1);
                if (last != null) return last;
                continue;
            }
            if (_root >= _scenes) return null;
            var list = SceneRoots[_root];
            if (_index >= list.Count) { _root++; _index = 0; continue; }
            var go = list[_index++];
            if (go != null) return go;
        }
    }

    // A new world: everything found before is gone.
    public static void Forget()
    {
        One_.Clear();
        MissedAt.Clear();
        foreach (var w in Wanted.Values) { w.Done = null; w.At = float.MinValue; w.Collecting = false; }
        _root = -1;
    }
}

// Element ids are only unique inside their place: every wild hare's carriable is b0e236a4-..., every deer has the
// same AI id. The place's own id comes first. Cached per object, so it stays the same after the game reparents it
// (a carried animal moves into the player's rig).
// Names with sibling indices up to the root: the same object has the same path on every copy of the world.
// A generated map has the same place several times over (five hare traps, three stuck Lutki), each a root of the same
// name: a root that is a place carries the place's id, "POI_TrappedHare(Clone)#<id>[0]".
public static class ScenePath
{
    private static readonly Dictionary<System.IntPtr, string> RootIds = new();

    // A piece built into the house is named after the moment it was placed on that copy
    // ("BB_Chicken_Skeleton_Chair(Clone)_00:02:27.7450000"); the same piece has another moment on another copy.
    private static string Plain(string name)
    {
        int at = name.LastIndexOf(")_");
        if (at < 0 || name.Length - at < 10 || !char.IsDigit(name[at + 2]) || name.IndexOf(':', at) < 0) return name;
        return name[..(at + 1)];
    }

    private static string RootId(Transform root)
    {
        if (RootIds.TryGetValue(root.Pointer, out var id)) return id;
        if (RootIds.Count > 4000) RootIds.Clear();
        var poi = root.GetComponent<POI_SaveLoad>();
        id = poi != null ? poi.uniqueID : null;
        if (string.IsNullOrEmpty(id)) return "";   // not cached: a place gets its id a little after it appears
        return RootIds[root.Pointer] = id;
    }

    public static string Of(Transform t)
    {
        var parts = new List<string>();
        for (; t != null; t = t.parent)
        {
            string id = t.parent == null ? RootId(t) : "";
            string name = Plain(t.name);
            parts.Add(id.Length > 0 ? $"{name}#{id}[{t.GetSiblingIndex()}]" : $"{name}[{t.GetSiblingIndex()}]");
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // The object at a path from Of, or null. A sibling index that differs (an object added before it on this copy)
    // falls back to the first child of that name.
    public static Transform Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split("]/");
        Transform at = null;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].EndsWith("]") ? parts[i][..^1] : parts[i];
            int open = part.LastIndexOf('[');
            if (open < 0 || !int.TryParse(part[(open + 1)..], out int index)) return null;
            var name = part[..open];
            if (i == 0)
            {
                int hash = name.LastIndexOf('#');
                at = hash > 0 ? Root(name[..hash], index, name[(hash + 1)..]) : Root(name, index, null);
            }
            else at = Child(at, name, index);
            if (at == null) return null;
        }
        return at;
    }

    private static Transform Root(string name, int index, string id)
    {
        Transform byName = null;
        for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name != name) continue;
                if (id != null)
                {
                    if (RootId(go.transform) == id) return go.transform;
                    continue;   // another place of the same kind: never the wrong one
                }
                if (go.transform.GetSiblingIndex() == index) return go.transform;
                byName ??= go.transform;
            }
        }
        return byName;
    }

    private static Transform Child(Transform parent, string name, int index)
    {
        if (index < parent.childCount && Plain(parent.GetChild(index).name) == name) return parent.GetChild(index);
        for (int i = 0; i < parent.childCount; i++) if (Plain(parent.GetChild(i).name) == name) return parent.GetChild(i);
        return null;
    }
}

public static class ElementKey
{
    private static readonly System.Collections.Generic.Dictionary<System.IntPtr, (string key, Transform place)> Cache = new();

    public static string Of(Component c, string elementId) => Get(c, elementId).key;
    public static Transform PlaceOf(Component c, string elementId) => Get(c, elementId).place;

    private static (string key, Transform place) Get(Component c, string elementId)
    {
        if (c == null || string.IsNullOrEmpty(elementId)) return (null, null);
        if (Cache.TryGetValue(c.Pointer, out var hit)) return hit;
        var poi = c.GetComponentInParent<POI_SaveLoad>(true);
        var v = (poi != null ? poi.uniqueID + "/" + elementId : elementId, poi != null ? poi.transform : null);
        Cache[c.Pointer] = v;
        return v;
    }

    public static void Clear() => Cache.Clear();
}
