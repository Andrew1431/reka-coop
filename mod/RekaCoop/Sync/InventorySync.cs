using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Everyone shares one pack: items, money, building blocks and plans, and clothes, kept in the host's save. Each copy
// watches its own pack and sends what changed ("+3 wood", "-10 logs"); the host applies a guest's changes and passes
// them on, so two players picking up or spending at once both count. Every few seconds the host sends its whole pack,
// and a guest whose own changes have all been applied there corrects any difference.
public static class InventorySync
{
    private enum Kind : byte { Hello = 1, Full = 2, Delta = 3 }

    // Item counts by id (both grids and the overflow together), money, building block instances by id, plans, clothes.
    public class Pack
    {
        public Dictionary<string, int> Items = new(), Blocks = new();
        public int Money;
        public HashSet<string> Plans = new(), Wear = new();

        public bool IsEmpty => Items.Count == 0 && Blocks.Count == 0 && Money == 0 && Plans.Count == 0 && Wear.Count == 0;

        // What turns `from` into this (plans and clothes are only ever gained).
        public Pack Minus(Pack from)
        {
            var d = new Pack { Money = Money - from.Money };
            Diff(Items, from.Items, d.Items);
            Diff(Blocks, from.Blocks, d.Blocks);
            d.Plans.UnionWith(Plans.Except(from.Plans));
            d.Wear.UnionWith(Wear.Except(from.Wear));
            return d;
        }

        private static void Diff(Dictionary<string, int> now, Dictionary<string, int> before, Dictionary<string, int> into)
        {
            foreach (var (id, n) in now) if (n != before.GetValueOrDefault(id)) into[id] = n - before.GetValueOrDefault(id);
            foreach (var (id, n) in before) if (!now.ContainsKey(id) && n != 0) into[id] = -n;
        }

        public void Write(NetDataWriter w)
        {
            w.Put(Money);
            foreach (var map in new[] { Items, Blocks })
            {
                w.Put(map.Count);
                foreach (var (id, n) in map) { w.Put(id); w.Put(n); }
            }
            foreach (var set in new[] { Plans, Wear })
            {
                w.Put(set.Count);
                foreach (var id in set) w.Put(id);
            }
        }

        public static Pack Read(NetDataReader r)
        {
            var p = new Pack { Money = r.GetInt() };
            foreach (var map in new[] { p.Items, p.Blocks })
            {
                int n = r.GetInt();
                for (int i = 0; i < n; i++) map[r.GetString()] = r.GetInt();
            }
            foreach (var set in new[] { p.Plans, p.Wear })
            {
                int n = r.GetInt();
                for (int i = 0; i < n; i++) set.Add(r.GetString());
            }
            return p;
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Money != 0) parts.Add($"money {Money:+0;-0}");
            parts.AddRange(Items.Select(kv => $"{kv.Value:+0;-0} {kv.Key}"));
            parts.AddRange(Blocks.Select(kv => $"{kv.Value:+0;-0} {kv.Key}"));
            parts.AddRange(Plans.Select(id => $"plan {id}"));
            parts.AddRange(Wear.Select(id => $"wear {id}"));
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }
    }

    private const float CheckEvery = 0.5f, FullEvery = 3f;

    private static readonly Dictionary<int, string> KeyOfPlayer = new();  // host: player id -> key, to drop stale connections
    private static readonly Dictionary<int, int> AppliedSeq = new();     // host: last change applied per guest
    private static Pack _base;                                            // the pack as last reported or applied
    private static bool _asked, _ready;
    private static int _seq;
    private static float _nextCheck, _nextFull, _settledAt, _toldAt = -10;
    internal static bool Applying;

    // Steam ID, plus the dev instance name because the local test copies share one Steam account.
    public static string LocalKey
    {
        get
        {
            string id = "nosteam";
            try { if (Steamworks.SteamClient.IsValid) id = Steamworks.SteamClient.SteamId.Value.ToString(); } catch { }
            return Instance.Name != null ? $"{id}-{Instance.Name}" : id;
        }
    }

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Inventory) OnMessage(session, from, r); };
        session.PlayerLeft += id => { KeyOfPlayer.Remove(id); AppliedSeq.Remove(id); };
    }

    // Leaving the world: a guest asks again after the next load; the host starts watching afresh.
    public static void ResetLocal()
    {
        _asked = _ready = false;
        _wearables = null;
        IdOf.Clear();
        _base = null;
    }

    public static void Tick(CoopSession session)
    {
        if (Manager == null) return;
        // While a save loads the pack holds whatever that save had: neither ask nor report until it settles.
        // (worldIsLoading stays set on guests long after loading, so only isLoading is used.)
        if (SaveLoadGame.isLoading) { _settledAt = Time.unscaledTime + 3f; return; }
        if (Time.unscaledTime < _settledAt) return;

        if (session.Role == Role.Host)
        {
            if (Time.unscaledTime >= _nextCheck) { _nextCheck = Time.unscaledTime + CheckEvery; Flush(session); }
            if (session.IsConnected && Time.unscaledTime >= _nextFull)
            {
                _nextFull = Time.unscaledTime + FullEvery;
                var all = Capture();
                if (all != null)
                    foreach (var p in session.Players.Values.Where(p => p.Ready && KeyOfPlayer.ContainsKey(p.PlayerId)).ToList())
                        SendFull(session, p.PlayerId, all);
            }
            return;
        }

        if (session.Role != Role.Client || !session.IsConnected) return;
        if (!_asked)
        {
            _asked = true;
            var w = session.BeginMessage(MsgType.Inventory);
            w.Put((byte)Kind.Hello);
            w.Put(LocalKey);
            session.Send(w, DeliveryMethod.ReliableOrdered);
            return;
        }
        if (_ready && Time.unscaledTime >= _nextCheck) { _nextCheck = Time.unscaledTime + CheckEvery; Flush(session); }
    }

    // Send whatever changed here since the last look.
    private static void Flush(CoopSession session)
    {
        var now = Capture();
        if (now == null) return;
        if (_base == null) { _base = now; return; }
        var d = now.Minus(_base);
        _base = now;
        if (d.IsEmpty || !session.IsConnected) return;
        var w = session.BeginMessage(MsgType.Inventory);
        w.Put((byte)Kind.Delta);
        w.Put(session.Role == Role.Host ? 0 : ++_seq);
        d.Write(w);
        session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Inventory: {d.Describe()}");
    }

    private static void SendFull(CoopSession session, int playerId, Pack all)
    {
        var w = session.BeginMessage(MsgType.Inventory);
        w.Put((byte)Kind.Full);
        w.Put(AppliedSeq.GetValueOrDefault(playerId));
        all.Write(w);
        session.SendTo(playerId, w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (session.Role == Role.Host && kind == Kind.Hello)
        {
            string key = r.GetString();
            // Someone who crashed and came back: their old connection hasn't timed out yet.
            foreach (var (other, otherKey) in KeyOfPlayer.ToList())
                if (other != from.PlayerId && otherKey == key) session.Kick(other);
            KeyOfPlayer[from.PlayerId] = key;
            AppliedSeq[from.PlayerId] = 0;
            Flush(session);
            var all = Capture();
            if (all != null) SendFull(session, from.PlayerId, all);
            Plugin.Logger.LogInfo($"Inventory: player {from.PlayerId} shares the pack");
        }
        else if (kind == Kind.Delta)
        {
            int seq = r.GetInt();
            var d = Pack.Read(r);
            Flush(session);  // our own changes first, so they aren't taken for theirs
            Apply(d);
            _base = Capture();
            if (session.Role == Role.Host)
            {
                AppliedSeq[from.PlayerId] = seq;
                var w = session.BeginMessage(MsgType.Inventory);
                w.Put((byte)Kind.Delta);
                w.Put(0);
                d.Write(w);
                session.Send(w, DeliveryMethod.ReliableOrdered, from.PlayerId);
            }
            Plugin.Logger.LogInfo($"Inventory: player {from.PlayerId}: {d.Describe()}");
        }
        else if (session.Role == Role.Client && kind == Kind.Full)
        {
            int applied = r.GetInt();
            var all = Pack.Read(r);
            if (!_ready)
            {
                // Joining: the host's save may be older than its pack.
                var fix = all.Minus(Capture() ?? new Pack());
                if (!fix.IsEmpty) Apply(fix);
                _base = Capture();
                _ready = true;
                _seq = 0;
                Plugin.Logger.LogInfo($"Inventory: sharing the host's pack ({fix.Describe()} to match)");
                return;
            }
            if (applied != _seq) return;  // the host hasn't seen all our changes yet
            Flush(session);
            if (applied != _seq) return;
            var diff = all.Minus(Capture() ?? new Pack());
            if (diff.IsEmpty) return;
            Apply(diff);
            _base = Capture();
            Plugin.Logger.LogInfo($"Inventory: corrected to the host's pack: {diff.Describe()}");
        }
    }

    private static InventoryManager Manager => RekaCoop.Sync.Find.One<InventoryManager>();
    private static PossessionTypeReference Ids => Manager?.pRef;
    private static PlayerBBCollection Blocks => RekaCoop.Sync.Find.One<BB_Building>()?.playerBBs;
    private static PlayerWearableCollection _wearables;
    private static PlayerWearableCollection Wearables => _wearables != null ? _wearables : _wearables = Resources.FindObjectsOfTypeAll<PlayerWearableCollection>().FirstOrDefault();
    private static readonly Dictionary<IntPtr, string> IdOf = new();  // possession -> id; the lookup is a list search

    private static string Id(PossessionTypeReference ids, Possession p)
    {
        if (IdOf.TryGetValue(p.Pointer, out var id)) return id;
        return IdOf[p.Pointer] = ids.TypeToID(p);
    }

    public static Pack Capture()
    {
        var m = Manager;
        var ids = m?.pRef;
        if (ids == null) return null;
        // Read straight from the game's inventories: going through its save state cost up to 45 ms a look.
        var money = RekaCoop.Sync.Find.One<MoneyManager>();
        if (money == null) return null;
        var p = new Pack { Money = money.currentMoney };
        foreach (var inv in new Inventory[] { m.inventory_collected, m.inventory_crafted, m.excessInventory })
            if (inv?.possessionsInInventory != null)
                foreach (var stack in inv.possessionsInInventory)
                    if (stack?._type != null && stack._amount != 0)
                    {
                        var id = Id(ids, stack._type);
                        p.Items[id] = p.Items.GetValueOrDefault(id) + stack._amount;
                    }
        var bbs = Blocks;
        if (bbs?.AquiredBBs != null)
            foreach (var b in bbs.AquiredBBs)
            {
                if (b?.bb == null) continue;
                var id = Id(ids, b.bb);
                if (b.amountOfInstances != 0) p.Blocks[id] = b.amountOfInstances;
                if (b.hasPlan) p.Plans.Add(id);
            }
        var wear = Wearables;
        if (wear?.AquiredWearables != null)
            foreach (var w in wear.AquiredWearables)
                if (w != null) p.Wear.Add(Id(ids, w));
        return p;
    }

    // Through the game's own add/remove calls, so the pack's layout and the HUD stay the game's.
    private static void Apply(Pack d)
    {
        var m = Manager;
        var ids = m?.pRef;
        if (ids == null) return;
        Applying = true;
        var missing = new List<string>();
        try
        {
            foreach (var (id, n) in d.Items)
            {
                var item = ids.IDToType(id)?.TryCast<Item>();
                if (item == null) { missing.Add(id); continue; }
                if (n > 0) m.AddItemsToPlayerInventory(item, n, true);
                else Take(m, item, -n);
            }
            if (d.Money != 0)
            {
                var money = RekaCoop.Sync.Find.One<MoneyManager>();
                if (money == null) missing.Add("money");
                else if (d.Money > 0) money.AddMoney(d.Money);
                else if (!money.RemoveMoney(-d.Money)) money.RemoveMoney(Math.Min(-d.Money, money.currentMoney));
            }
            var bbs = Blocks;
            foreach (var (id, n) in d.Blocks)
            {
                var bb = ids.IDToType(id)?.TryCast<BuildingBlock>();
                if (bb == null || bbs == null) { missing.Add(id); continue; }
                if (n > 0) bbs.AddAsInstance(bb, n, false, true);
                else for (int i = 0; i < -n && bbs.GetAmountOfInstances(bb) > 0; i++) bbs.RemoveInstance(bb, false, false);
            }
            foreach (var id in d.Plans)
            {
                var bb = ids.IDToType(id)?.TryCast<BuildingBlock>();
                if (bb == null || bbs == null) { missing.Add(id); continue; }
                if (!bbs.HasPlan(bb)) bbs.AddAsPlan(bb, true, true, true);
            }
            var wear = Wearables;
            foreach (var id in d.Wear)
            {
                var w = ids.IDToType(id)?.TryCast<WearableMesh>();
                if (w == null || wear == null) { missing.Add(id); continue; }
                if (!wear.HasWearable(w)) wear.AddWearable(w);
            }
            m.OnInventoryChanged();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Inventory: applying {d.Describe()} failed: {e.Message}"); }
        finally { Applying = false; }
        if (missing.Count > 0 && Time.unscaledTime - _toldAt > 30)
        {
            _toldAt = Time.unscaledTime;
            Plugin.Logger.LogWarning($"Inventory: couldn't apply {string.Join(", ", missing)}");
        }
    }

    // The grids first, then the overflow.
    private static void Take(InventoryManager m, Item item, int amount)
    {
        int inGrids = Math.Min(amount, m.GetItemAmount(item));
        if (inGrids > 0 && !m.RemoveItemsFromPlayerInventory(item, inGrids))
            for (int i = 0; i < inGrids && m.RemoveItemsFromPlayerInventory(item, 1); i++) { }
        int rest = amount - inGrids;
        if (rest > 0) m.TakeFromExcess(item, rest);
    }

    internal static Dictionary<string, string> Snapshot()
    {
        if (Capture() is not { } p) return null;
        var all = new Dictionary<string, string> { ["money"] = p.Money.ToString() };
        foreach (var (id, n) in p.Items) all[$"item:{id}"] = n.ToString();
        foreach (var (id, n) in p.Blocks) all[$"block:{id}"] = n.ToString();
        foreach (var id in p.Plans) all[$"plan:{id}"] = "1";
        foreach (var id in p.Wear) all[$"wear:{id}"] = "1";
        return all;
    }

    public static string Describe() => $"ready={_ready} seq={_seq} base={(_base == null ? "none" : "set")}; " + (Capture() is { } p ? $"money {p.Money}, " + string.Join(", ", p.Items.Select(kv => $"{kv.Value} {kv.Key}")) + $"; {p.Blocks.Count} block kinds, {p.Plans.Count} plans, {p.Wear.Count} clothes" : "no pack");
}
