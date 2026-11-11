using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Story events switch objects on and off: a quest step shows the crows you're meant to talk to, a scene puts a book on
// the table, a trigger that has fired turns itself off. None of that is in the save until the next one, and the
// events only run on the host. So the host watches what's on and off under the story's scene roots (the tutorial's
// props, places, the village, the ritual site) and guests follow its changes; a guest that joins gets every change
// since the host's world loaded. Characters (AI, animators) are shown and hidden as a whole; their parts are their
// own. The hut, the UI and the players are left out.
public static class ActiveSync
{
    private enum Kind : byte { Ready = 1, Changes = 2 }

    private static readonly HashSet<string> SkipRoots = new()
    {
        "Main Canvas", "PlayerCameraSetup", "BackgroundImages", "UseItems", "Ground contact decals", "WeatherManager", "Scripts",
        "Home", "MapBoundaries", "Unused Overlay UI",  // interaction prompts, pooled per copy
    };
    private const int MinRootSize = 100;   // vegetation and small props are their own roots
    private const int CheckPerFrame = 400;

    private sealed class Node { public string Key; public GameObject Go; public bool On; }

    private static List<Node> _nodes;                 // indexed in this world
    private static Dictionary<string, Node> _byKey;
    private static Stack<(Transform t, string key)> _pending;
    private static List<Node> _building;
    private static int _next;
    private static bool _readySent;
    private static readonly Dictionary<string, bool> Changed = new();   // host: every change since the world loaded
    private static readonly HashSet<int> Waiting = new();
    private static readonly List<(string, bool)> Outgoing = new();
    private static readonly Dictionary<string, bool> Early = new();   // guest: the host's changes that came before our index
    private static float _nextFlush;
    internal static bool Applying;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Active) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _nodes = null;
        _byKey = null;
        _pending = null;
        _building = null;
        _next = 0;
        _readySent = false;
        Changed.Clear();
        Outgoing.Clear();
        Early.Clear();
    }

    // Carriable things too: their insides (colliders, hint effects) switch with who holds them, which differs by copy;
    // switching the host's collider on inside a guest's arms shoved the guest 100 m away.
    private static bool Skip(Transform t) =>
        t.GetComponent<GeneralAIController>() != null || t.GetComponent<Animator>() != null || t.GetComponent<Canvas>() != null
        || t.GetComponent<CarriableObject>() != null
        // A tool's own parts show while its player holds it (the ritual's charm lives in the ritual, not on the player).
        || (t.parent != null && t.parent.GetComponent<BaseUseItem>() != null);

    // Indexes the story roots a few hundred objects a frame; true once done.
    private static bool Indexed()
    {
        if (_nodes != null) return true;
        if (_pending == null)
        {
            _pending = new Stack<(Transform, string)>();
            _building = new List<Node>();
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                var names = roots.GroupBy(g => g.name).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
                foreach (var go in roots)
                {
                    if (SkipRoots.Contains(go.name) || go.name.StartsWith("RekaCoop") || !names.Contains(go.name)) continue;
                    if (go.transform.childCount == 0 || go.GetComponentsInChildren<Transform>(true).Length < MinRootSize) continue;
                    _pending.Push((go.transform, go.name));
                }
            }
            return false;
        }
        for (int n = 0; n < CheckPerFrame && _pending.Count > 0; n++)
        {
            var (t, key) = _pending.Pop();
            if (t == null) continue;
            _building.Add(new Node { Key = key, Go = t.gameObject, On = t.gameObject.activeSelf });
            if (Skip(t))  // a character: shown or hidden as a whole, its insides are its own
            {
                // ...but whether it can be talked to is story (the old woman, once the yard gate has shut behind you)
                if (t.GetComponent<GeneralAIController>() != null)
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        if (c.GetComponent<PlayerDetection>() != null)
                            _building.Add(new Node { Key = $"{key}/{c.name}[{i}]", Go = c.gameObject, On = c.gameObject.activeSelf });
                    }
                continue;
            }
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                _pending.Push((c, $"{key}/{c.name}[{i}]"));
            }
        }
        if (_pending.Count > 0) return false;
        _nodes = _building;
        _byKey = new Dictionary<string, Node>();
        foreach (var node in _nodes) _byKey[node.Key] = node;
        _building = null;
        _pending = null;
        Plugin.Logger.LogInfo($"Active: watching {_nodes.Count} story objects");
        if (Early.Count > 0)
        {
            var (applied, missing) = ApplyAll(Early.Select(kv => (kv.Key, kv.Value)).ToList());
            Plugin.Logger.LogInfo($"Active: {Early.Count} early changes from the host, {applied} switched here{(missing > 0 ? $", {missing} not found" : "")}");
            Early.Clear();
        }
        return true;
    }

    // Called once the local body is in the world.
    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || SaveLoadGame.isLoading || !Indexed()) return;
        if (session.Role == Role.Client)
        {
            if (!_readySent)
            {
                _readySent = true;
                var w = session.BeginMessage(MsgType.Active);
                w.Put((byte)Kind.Ready);
                session.Send(w, DeliveryMethod.ReliableOrdered);
            }
            return;
        }
        foreach (var p in Waiting) Send(session, Changed.Select(kv => (kv.Key, kv.Value)).ToList(), p);
        Waiting.Clear();
        if (_nodes.Count == 0) return;
        for (int n = 0; n < CheckPerFrame; n++)
        {
            if (_next >= _nodes.Count) _next = 0;
            var node = _nodes[_next++];
            if (node.Go == null) continue;
            bool on = node.Go.activeSelf;
            if (on == node.On) continue;
            node.On = on;
            Changed[node.Key] = on;
            Outgoing.Add((node.Key, on));
        }
        // Changes go out together, so a step that switches many things arrives as one.
        if (Outgoing.Count == 0 || Time.unscaledTime < _nextFlush) return;
        _nextFlush = Time.unscaledTime + 0.25f;
        Send(session, Outgoing.ToList(), -1);
        Plugin.Logger.LogInfo($"Active: sent {Outgoing.Count}: {string.Join(", ", Outgoing.Take(4).Select(c => $"{(c.Item2 ? "+" : "-")}{Short(c.Item1)}"))}");
        Outgoing.Clear();
    }

    private static string Short(string key)
    {
        var parts = key.Split('/');
        return parts.Length <= 3 ? key : $"{parts[0]}/../{parts[^2]}/{parts[^1]}";
    }

    private static void Send(CoopSession session, List<(string key, bool on)> changes, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Active);
        w.Put((byte)Kind.Changes);
        w.Put(changes.Count);
        foreach (var (key, on) in changes) { w.Put(key); w.Put(on); }
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            if (_nodes == null) Waiting.Add(from.PlayerId);
            else Send(session, Changed.Select(kv => (kv.Key, kv.Value)).ToList(), from.PlayerId);
        }
        else if (kind == Kind.Changes && session.Role == Role.Client)
        {
            int n = r.GetInt();
            var changes = new List<(string, bool)>(n);
            for (int i = 0; i < n; i++) changes.Add((r.GetString(), r.GetBool()));
            if (_byKey == null)
            {
                foreach (var (key, on) in changes) Early[key] = on;
                return;
            }
            var (applied, missing) = ApplyAll(changes);
            Plugin.Logger.LogInfo($"Active: host's {n}, {applied} switched here{(missing > 0 ? $", {missing} not found" : "")}");
        }
    }

    private static (int applied, int missing) ApplyAll(List<(string key, bool on)> changes)
    {
        int applied = 0, missing = 0;
        Applying = true;
        WorldObjects.ApplyingRemote = true;
        try
        {
            foreach (var (key, on) in changes)
            {
                if (!_byKey.TryGetValue(key, out var node) || node.Go == null) { missing++; continue; }
                node.On = on;
                if (node.Go.activeSelf == on) continue;
                try { node.Go.SetActive(on); applied++; }
                catch (Exception e) { Plugin.Logger.LogWarning($"Active: {Short(key)}: {e.InnerException?.Message ?? e.Message}"); }
            }
        }
        finally
        {
            WorldObjects.ApplyingRemote = false;
            Applying = false;
        }
        return (applied, missing);
    }

    // Guest: the desync detector found objects on or off here unlike the host's (something local switched them, so no
    // change from the host ever came): match the host's. Keys only one side has are left alone.
    internal static int MatchHost(IEnumerable<(string key, bool on)> hostValues)
    {
        if (_byKey == null) return 0;
        var list = new List<(string, bool)>(hostValues);
        var (applied, _) = ApplyAll(list);
        return applied;
    }

    // For DigestSync, read lazily so it can spread the work over frames.
    internal static IEnumerable<KeyValuePair<string, string>> Snapshot() => _nodes == null ? null : Walk(_nodes);

    private static IEnumerable<KeyValuePair<string, string>> Walk(List<Node> nodes)
    {
        foreach (var node in nodes)
            // A villager's held tool follows what its own AI is doing on each copy: noise, not story. Things a place
            // spawns below itself while running (a ring's spots to sweep) exist on a copy only if they were there
            // when it indexed; UseItemSync and the place's own state keep those in step.
            if (node.Go != null && !node.Key.Contains("PropFollower") && !node.Key.Contains("(Clone)[")) yield return new(node.Key, node.Go.activeSelf ? "1" : "0");
    }

    public static string Describe() => _nodes == null ? "indexing" : $"{_nodes.Count} watched, {Changed.Count} changed since load";
}
