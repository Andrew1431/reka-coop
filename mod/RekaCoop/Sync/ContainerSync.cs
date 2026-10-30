using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Chests and other containers hold the same things for everyone. A container is named by where it stands (relative
// to the hut for the hut's own chests, which move with it); each machine compares contents a few times a second and
// sends a container's whole contents when they change. The host applies and relays a guest's, and sends everything
// to a guest that has just loaded. Without this, what a guest puts in a chest would only exist on the guest's copy
// and vanish at the next reload.
public static class ContainerSync
{
    private enum Kind : byte { Contents = 1, Ready = 2, Snapshot = 3 }

    private class Pending { public string Key; public int Slots; public List<(string id, int amount)> Items; public float Since; }

    private static Dictionary<string, string> _known;   // key -> contents signature, as agreed
    private static readonly List<Pending> Waiting = new();
    private static float _nextPoll;
    private static bool _readySent;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Container) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _known = null;
        Waiting.Clear();
        _readySent = false;
        _all = null;
    }

    private static string KeyOf(InteractableInventoryLogic c)
    {
        var home = BB_Building.Home;
        var t = c.transform;
        if (home != null && t.IsChildOf(home))
        {
            var p = home.InverseTransformPoint(t.position);
            return $"H:{c.name}@{Mathf.RoundToInt(p.x * 100)},{Mathf.RoundToInt(p.y * 100)},{Mathf.RoundToInt(p.z * 100)}";
        }
        var w = t.position;
        return $"W:{c.name}@{Mathf.RoundToInt(w.x * 10)},{Mathf.RoundToInt(w.y * 10)},{Mathf.RoundToInt(w.z * 10)}";
    }

    private static List<(string id, int amount)> Items(InteractableInventoryLogic c)
    {
        var list = new List<(string, int)>();
        var state = c.GetSerializedState();
        if (state?.possessionsInInventory != null)
            foreach (var s in state.possessionsInInventory) list.Add((s._typeID ?? "", s._amount));
        return list;
    }

    private static string Signature(List<(string id, int amount)> items) => string.Join(";", items.Select(i => $"{i.id}x{i.amount}"));

    private static Dictionary<string, string> Signatures(Dictionary<string, InteractableInventoryLogic> all)
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in all) d[kv.Key] = Signature(Items(kv.Value));
        return d;
    }

    private static Dictionary<string, InteractableInventoryLogic> _all;
    private static List<(string key, InteractableInventoryLogic c)> _order = new();
    private static InteractableInventoryLogic[] _allFrom;
    private static int _cursor;

    // From the scene scan, renewed every 10 s; a fresh search (slow) only when a guest joins.
    private static Dictionary<string, InteractableInventoryLogic> All(bool fresh = false)
    {
        var scanned = RekaCoop.Sync.Find.Scanned<InteractableInventoryLogic>(10f);
        if (!fresh && _all != null && (scanned == _allFrom || scanned == null)) return _all;
        _allFrom = scanned;
        var all = _all = new Dictionary<string, InteractableInventoryLogic>();
        foreach (var c in fresh || scanned == null ? Object.FindObjectsByType<InteractableInventoryLogic>(FindObjectsSortMode.None) : (IEnumerable<InteractableInventoryLogic>)scanned)
            if (c.MyInventory != null && c.GetSerializedState() != null) all[KeyOf(c)] = c;
        _order = all.Select(kv => (kv.Key, kv.Value)).ToList();
        return all;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || !HouseBlocks.Ready || RekaCoop.Sync.Find.Scanned<InteractableInventoryLogic>(10f) == null) return;
        var all = All();

        if (_known == null)
        {
            if (session.Role == Role.Host) { _known = Signatures(all); return; }
            if (_readySent) return;
            _readySent = true;
            var rw = session.BeginMessage(MsgType.Container);
            rw.Put((byte)Kind.Ready);
            session.Send(rw, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (Time.unscaledTime >= _nextPoll)
        {
            _nextPoll = Time.unscaledTime + 0.5f;
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                var p = Waiting[i];
                if (all.TryGetValue(p.Key, out var c) && c != null && Apply(c, p.Key, p.Slots, p.Items)) Waiting.RemoveAt(i);
                else if (Time.unscaledTime - p.Since > 20f) { Plugin.Logger.LogWarning($"Containers: no {p.Key} here; dropped its contents"); Waiting.RemoveAt(i); }
            }
        }

        // A couple of containers a frame.
        for (int step = 0; step < 2 && _order.Count > 0; step++)
        {
            if (_cursor >= _order.Count) _cursor = 0;
            var (key, c) = _order[_cursor++];
            if (c == null) continue;
            var items = Items(c);
            var sig = Signature(items);
            if (_known.TryGetValue(key, out var k) && k == sig) continue;
            bool isNew = !_known.ContainsKey(key);
            _known[key] = sig;
            if (isNew) continue;  // a container that just appeared (built, map streamed in) is not a change
            var w = session.BeginMessage(MsgType.Container);
            w.Put((byte)Kind.Contents);
            Write(w, key, c.MyInventory.inventorySlots, items);
            session.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Containers: {key} now holds {Describe(items)}");
        }
    }

    private static void Write(NetDataWriter w, string key, int slots, List<(string id, int amount)> items)
    {
        w.Put(key);
        w.Put(slots);
        w.Put(items.Count);
        foreach (var (id, amount) in items) { w.Put(id); w.Put(amount); }
    }

    private static (string key, int slots, List<(string, int)> items) Read(NetDataReader r)
    {
        string key = r.GetString();
        int slots = r.GetInt(), n = r.GetInt();
        var items = new List<(string, int)>();
        for (int i = 0; i < n; i++) items.Add((r.GetString(), r.GetInt()));
        return (key, slots, items);
    }

    private static string Describe(List<(string id, int amount)> items) =>
        items.Count(i => i.amount > 0) == 0 ? "nothing" : string.Join(", ", items.Where(i => i.amount > 0).Select(i => $"{i.amount} {i.id}"));

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var all = All(fresh: true);
            var w = session.BeginMessage(MsgType.Container);
            w.Put((byte)Kind.Snapshot);
            w.Put(all.Count);
            foreach (var (key, c) in all) Write(w, key, c.MyInventory.inventorySlots, Items(c));
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Containers: sent {all.Count} containers to player {from.PlayerId}");
        }
        else if (kind == Kind.Snapshot)
        {
            int n = r.GetInt(), changed = 0;
            var all = All(fresh: true);
            _known = Signatures(all);
            for (int i = 0; i < n; i++)
            {
                var (key, slots, items) = Read(r);
                if (_known.TryGetValue(key, out var k) && k == Signature(items)) continue;
                changed++;
                if (!all.TryGetValue(key, out var c) || !Apply(c, key, slots, items))
                    Waiting.Add(new Pending { Key = key, Slots = slots, Items = items, Since = Time.unscaledTime });
            }
            Plugin.Logger.LogInfo($"Containers: host's {n} containers, {changed} differed here");
        }
        else if (kind == Kind.Contents)
        {
            var (key, slots, items) = Read(r);
            if (session.Role == Role.Host)
            {
                var w = session.BeginMessage(MsgType.Container);
                w.Put((byte)Kind.Contents);
                Write(w, key, slots, items);
                session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
            }
            if (_known == null) return;  // the snapshot will carry it
            if (!All().TryGetValue(key, out var c) || !Apply(c, key, slots, items))
                Waiting.Add(new Pending { Key = key, Slots = slots, Items = items, Since = Time.unscaledTime });
            Plugin.Logger.LogInfo($"Containers: player {from.PlayerId} changed {key}: {Describe(items)}");
        }
    }

    // Through the game's own load path, keeping the container's allowed item groups.
    private static bool Apply(InteractableInventoryLogic c, string key, int slots, List<(string id, int amount)> items)
    {
        var state = c.GetSerializedState();
        if (state == null) return false;  // not set up yet
        state.inventorySlots = slots;
        var list = new Il2CppSystem.Collections.Generic.List<SaveGame.SerializablePossessionStack>();
        foreach (var (id, amount) in items) list.Add(new SaveGame.SerializablePossessionStack { _typeID = id, _amount = amount });
        state.possessionsInInventory = list;
        c.RestoreSerializedState(state);
        c.UpdateUI();
        if (_known != null) _known[key] = Signature(Items(c));
        return true;
    }

    // Dev: put items into the nearest container.
    public static string PutNearest(string id, int amount)
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        InteractableInventoryLogic best = null;
        float bestD = float.MaxValue;
        foreach (var c in All().Values)
        {
            float d = (c.transform.position - me.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) return "no container";
        var p = best.pRef != null ? best.pRef.IDToType(id) : null;
        if (p == null) return $"unknown item {id}";
        best.MyInventory.Add(p, amount, true);
        best.OnInvChange();
        return $"{KeyOf(best)} at {Mathf.Sqrt(bestD):0} m: {Describe(Items(best))}";
    }

    public static string DescribeAll() => string.Join("\n", All().Select(kv => $"{kv.Key}: {Describe(Items(kv.Value))}"));
}
