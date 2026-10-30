using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Pickups and destruction in the world happen for everyone. Every machine loaded the same map, so the same
// collectible or tree sits at the same spot everywhere (checked: all 1822 collectibles match); each is named by its
// type and position. Pieces that fall out of a destroyed object scatter randomly on each machine, so they are named
// by the object they came from and their index in it. Changes are applied locally at once and reported; everyone else
// repeats them the game's own way (damage for damage, removal without items for pickups), so the host's save
// records them as if they were local. Two players grabbing one item in the same instant: the host's order decides,
// and a guest whose grab came second is told so and gives its copy back.
public static class WorldObjects
{
    private enum Kind : byte { Collected = 1, Ready = 2, History = 3, Damage = 4, Denied = 5 }

    public readonly struct Key
    {
        public readonly string Type;   // item name, "D:<object name>" for destructibles, "...#<index>" for a piece
        public readonly Vector3 Pos;
        public readonly string Path;   // place in the scene, the same on every copy; "" if not known. Tells stacked items apart
        public Key(string type, Vector3 pos, string path = "") { Type = type; Pos = pos; Path = path ?? ""; }
        public override string ToString() => $"{Type}@{Pos.x:0.0},{Pos.y:0.0},{Pos.z:0.0}";
    }

    private class Change { public Kind Kind; public Key Key; public float Amount; public float Since = -1; public float At = Time.unscaledTime; }
    private const float RaceWindow = 10f;  // an item that grows back later may be taken again

    private const float MatchRadius = 0.5f;
    private const float PathRadius = 3f;  // loose items can be nudged apart by physics; the same place in the scene still matches

    private static readonly List<Change> History = new();  // this map, this session, in order (host replays it to joiners)
    private static readonly List<Change> Pending = new();  // received but not applied yet (object not there yet)
    private static readonly HashSet<System.IntPtr> Reported = new();
    private static readonly HashSet<string> TakenPaths = new();  // taken here; the object may be gone by the time the news arrives
    private static readonly Dictionary<System.IntPtr, Key> PieceOf = new();  // the game unlinks a piece from its object when it is taken
    private static bool _readySent;
    private static readonly List<(Key key, Item item, int amount, float at)> MyPickups = new();  // guest: recent, in case one is denied
    private static float _nextRetry;
    internal static bool ApplyingRemote { get; set; }

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.WorldObject) OnMessage(session, from, r); };
    }

    // Left the world: a new map has new objects.
    public static void ResetLocal()
    {
        History.Clear();
        Pending.Clear();
        Reported.Clear();
        TakenPaths.Clear();
        PieceOf.Clear();
        MyPickups.Clear();
        _readySent = false;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        if (session.Role == Role.Client && !_readySent)
        {
            _readySent = true;
            var w = session.BeginMessage(MsgType.WorldObject);
            w.Put((byte)Kind.Ready);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
        if (Pending.Count == 0 || Time.unscaledTime < _nextRetry) return;
        _nextRetry = Time.unscaledTime + 1f;
        // In order: a piece can only be picked up once its object has been destroyed.
        while (Pending.Count > 0)
        {
            var c = Pending[0];
            if (c.Since < 0) c.Since = Time.unscaledTime;
            if (!Apply(c))
            {
                if (Time.unscaledTime - c.Since < 15f) break;
                Plugin.Logger.LogWarning($"WorldObjects: gave up on someone's change, nothing matches here: {Describe(c)}");
            }
            Pending.RemoveAt(0);
        }
    }

    // Local pickup. Several game paths lead here for one pickup; only the first counts.
    internal static void OnLocalCollected(CollectibleID c)
    {
        if (ApplyingRemote || c == null || !Reported.Add(c.Pointer)) return;
        var key = KeyOf(c);
        if (key.Path != "") TakenPaths.Add(key.Path);
        if (CoopDriver.Session?.Role == Role.Client)
        {
            float now = Time.unscaledTime;
            MyPickups.RemoveAll(p => now - p.at > 30f);
            MyPickups.Add((key, c.type, c.amount, now));
        }
        Report(new Change { Kind = Kind.Collected, Key = key });
    }

    internal static void OnDestroyed(DestructibleObject d)
    {
        foreach (var (ptr, key) in PieceKeys(d)) PieceOf[ptr] = key;
    }

    internal static void OnLocalDamage(DestructibleObject d, float amount)
    {
        if (ApplyingRemote || d == null || amount <= 0) return;
        Report(new Change { Kind = Kind.Damage, Key = KeyOf(d), Amount = amount });
    }

    private static void Report(Change c)
    {
        var session = CoopDriver.Session;
        if (session == null || !session.IsConnected) return;
        History.Add(c);
        var w = session.BeginMessage(MsgType.WorldObject);
        Write(w, c);
        session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"WorldObjects: I {Describe(c)}");
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        switch (kind)
        {
            case Kind.Collected:
            case Kind.Damage:
            {
                var c = ReadBody(kind, r);
                if (session.Role == Role.Host && kind == Kind.Collected && AlreadyTaken(c.Key))
                {
                    var d = session.BeginMessage(MsgType.WorldObject);
                    d.Put((byte)Kind.Denied);
                    Write(d, c);
                    session.SendTo(from.PlayerId, d, DeliveryMethod.ReliableOrdered);
                    Plugin.Logger.LogInfo($"WorldObjects: player {from.PlayerId} also {Describe(c)}, but it was already taken; told them");
                    break;
                }
                if (session.Role == Role.Host)
                {
                    var w = session.BeginMessage(MsgType.WorldObject);
                    Write(w, c);
                    session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
                }
                History.Add(c);
                if (Pending.Count > 0 || !Apply(c)) Pending.Add(c);
                break;
            }
            case Kind.Denied:
                GiveBack(ReadBody((Kind)r.GetByte(), r).Key);
                break;
            case Kind.Ready when session.Role == Role.Host:
            {
                var w = session.BeginMessage(MsgType.WorldObject);
                w.Put((byte)Kind.History);
                w.Put(History.Count);
                foreach (var c in History) Write(w, c);
                session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
                Plugin.Logger.LogInfo($"WorldObjects: sent {History.Count} world changes to player {from.PlayerId}");
                break;
            }
            case Kind.History:
            {
                int count = r.GetInt();
                for (int i = 0; i < count; i++)
                {
                    var c = ReadBody((Kind)r.GetByte(), r);
                    History.Add(c);
                    Pending.Add(c);
                }
                Plugin.Logger.LogInfo($"WorldObjects: host reports {count} world changes this session");
                break;
            }
        }
    }

    private static bool Same(Key a, Key b) => a.Type == b.Type &&
        (a.Path != "" && b.Path != "" ? a.Path == b.Path && (a.Pos - b.Pos).sqrMagnitude < PathRadius * PathRadius : (a.Pos - b.Pos).sqrMagnitude < MatchRadius * MatchRadius);

    private static bool AlreadyTaken(Key key)
    {
        float now = Time.unscaledTime;
        foreach (var h in History) if (h.Kind == Kind.Collected && now - h.At < RaceWindow && Same(h.Key, key)) return true;
        return false;
    }

    // Guest: someone else got this item first; take ours back out of the pack.
    private static void GiveBack(Key key)
    {
        int i = MyPickups.FindIndex(p => Same(p.key, key));
        if (i < 0) { Plugin.Logger.LogWarning($"WorldObjects: host says {key} was taken first, but I don't remember picking it up"); return; }
        var (_, item, amount, _) = MyPickups[i];
        MyPickups.RemoveAt(i);
        var inv = RekaCoop.Sync.Find.One<InventoryManager>();
        bool removed = item != null && inv != null && inv.RemoveItemsFromPlayerInventory(item, amount);
        Plugin.Logger.LogInfo($"WorldObjects: {key} was taken by someone first; gave back {amount} ({removed})");
        if (removed) DialogueSync.ShowLocal("Someone else picked that up first.");
    }

    private static void Write(NetDataWriter w, Change c)
    {
        w.Put((byte)c.Kind);
        w.Put(c.Key.Type ?? ""); w.Put(c.Key.Pos.x); w.Put(c.Key.Pos.y); w.Put(c.Key.Pos.z); w.Put(c.Key.Path);
        if (c.Kind == Kind.Damage) w.Put(c.Amount);
    }

    private static Change ReadBody(Kind kind, NetDataReader r)
    {
        var c = new Change { Kind = kind, Key = new Key(r.GetString(), new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), r.GetString()) };
        if (kind == Kind.Damage) c.Amount = r.GetFloat();
        return c;
    }

    private static string Describe(Change c) => c.Kind == Kind.Damage ? $"hit {c.Key} for {c.Amount:0.#}" : $"took {c.Key}";

    private static string TypeOf(CollectibleID c) => c.type != null ? c.type.name : c.gameObject.name;

    public static Key KeyOf(DestructibleObject d) => new("D:" + d.gameObject.name, d.transform.position);

    public static Key KeyOf(CollectibleID c) =>
        PieceOf.TryGetValue(c.Pointer, out var k) || PieceKeys().TryGetValue(c.Pointer, out k) ? k : new Key(TypeOf(c), c.transform.position, ScenePath(c.transform));

    private static string ScenePath(Transform t) => Sync.ScenePath.Of(t);

    // Pieces of destroyed objects, by collectible.
    private static Dictionary<System.IntPtr, Key> PieceKeys()
    {
        var keys = new Dictionary<System.IntPtr, Key>();
        foreach (var d in Object.FindObjectsByType<DestructibleObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var (ptr, key) in PieceKeys(d)) keys[ptr] = key;
        return keys;
    }

    private static IEnumerable<(System.IntPtr, Key)> PieceKeys(DestructibleObject d)
    {
        if (!d.hasBeenDestroyed || d.collectibles == null) yield break;
        var k = KeyOf(d);
        for (int i = 0; i < d.collectibles.Count; i++)
            if (d.collectibles[i] != null) yield return (d.collectibles[i].Pointer, new Key($"{k.Type}#{i}", k.Pos));
    }

    // True once the change has happened here too (or there is nothing left to do).
    private static bool Apply(Change c)
    {
        if (c.Kind == Kind.Damage)
        {
            var d = FindDestructible(c.Key);
            if (d == null) return false;
            if (d.hasBeenDestroyed) return true;
            ApplyingRemote = true;
            try { d.DamageObject(c.Amount); }
            finally { ApplyingRemote = false; }
            Plugin.Logger.LogInfo($"WorldObjects: someone {Describe(c)}; hp now {d.currentHitPoints:0.#}, destroyed={d.hasBeenDestroyed}");
            return true;
        }

        if (c.Key.Path != "" && TakenPaths.Contains(c.Key.Path)) return true;
        var item = FindCollectible(c.Key);
        if (item == null) return false;
        if (c.Key.Path != "") TakenPaths.Add(c.Key.Path);
        Reported.Add(item.Pointer);
        if (!item.collected && item.gameObject.activeInHierarchy)
        {
            ApplyingRemote = true;
            try { item.DestroyCollectibleExternal(); }
            finally { ApplyingRemote = false; }
        }
        Plugin.Logger.LogInfo($"WorldObjects: someone {Describe(c)}");
        return true;
    }

    private static DestructibleObject FindDestructible(Key key)
    {
        DestructibleObject best = null;
        float bestD = MatchRadius * MatchRadius;
        foreach (var d in Object.FindObjectsByType<DestructibleObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if ("D:" + d.gameObject.name != key.Type) continue;
            float dist = (d.transform.position - key.Pos).sqrMagnitude;
            if (dist < bestD) { bestD = dist; best = d; }
        }
        return best;
    }

    private static CollectibleID FindCollectible(Key key)
    {
        int hash = key.Type.LastIndexOf('#');
        if (key.Type.StartsWith("D:") && hash > 0)
        {
            var d = FindDestructible(new Key(key.Type.Substring(0, hash), key.Pos));
            if (d == null || !d.hasBeenDestroyed) return null;
            OnDestroyed(d);
            foreach (var c in Object.FindObjectsByType<CollectibleID>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (PieceOf.TryGetValue(c.Pointer, out var k) && k.Type == key.Type && (k.Pos - key.Pos).sqrMagnitude < MatchRadius * MatchRadius) return c;
            return null;
        }
        // The same place in the scene first (taken here already counts: Apply sees it collected); by position only when
        // the sender's item has no counterpart by place here (e.g. a spawned drop).
        if (key.Path != "" && ByPath(key) is { } same) return same;
        CollectibleID best = null;
        float bestD = MatchRadius * MatchRadius;
        foreach (var c in Object.FindObjectsByType<CollectibleID>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (c.collected || TypeOf(c) != key.Type) continue;
            float dist = (c.transform.position - key.Pos).sqrMagnitude;
            if (dist < bestD) { bestD = dist; best = c; }
        }
        return best;
    }

    private static CollectibleID ByPath(Key key)
    {
        foreach (var c in Object.FindObjectsByType<CollectibleID>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (TypeOf(c) == key.Type && (c.transform.position - key.Pos).sqrMagnitude < PathRadius * PathRadius && ScenePath(c.transform) == key.Path) return c;
        return null;
    }

    public static string Dump()
    {
        var sb = new StringBuilder();
        var pieces = PieceKeys();
        foreach (var c in Object.FindObjectsByType<CollectibleID>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            sb.Append($"{(pieces.TryGetValue(c.Pointer, out var k) ? k : new Key(TypeOf(c), c.transform.position))}\t{c.gameObject.activeInHierarchy}\t{c.collected}\t{c.amount}\t{c.name}\t{ScenePath(c.transform)}\n");
        foreach (var d in Object.FindObjectsByType<DestructibleObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            sb.Append($"{KeyOf(d)}\t{d.gameObject.activeInHierarchy}\t{d.hasBeenDestroyed}\t{d.currentHitPoints:0.#}/{d.maxHitPoints:0.#}\ttree={d.isTree} pieces={d.collectibles?.Count}\n");
        return sb.ToString();
    }

    // Dev testing: collect the nearest collectible (optionally of a type), or hit the nearest destructible.
    public static string CollectNearest(string typeFilter = null)
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        if (me == null) return "no player";
        CollectibleID best = null;
        float bestD = float.MaxValue;
        foreach (var c in Object.FindObjectsByType<CollectibleID>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (c.collected || c.type == null || (typeFilter != null && !c.type.name.Contains(typeFilter))) continue;
            float d = (c.transform.position - me.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) return "nothing to collect";
        var key = KeyOf(best);
        return $"{key} at {Mathf.Sqrt(bestD):0.0} m: {best.TryCollectItem()}";
    }

    public static string HitNearest(float amount, string filter)
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        if (me == null) return "no player";
        DestructibleObject best = null;
        float bestD = float.MaxValue;
        foreach (var d in Object.FindObjectsByType<DestructibleObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (d.hasBeenDestroyed || !d.canTakeDamage || (filter == "tree" ? !d.isTree : filter != null && !d.name.Contains(filter))) continue;
            float dist = (d.transform.position - me.transform.position).sqrMagnitude;
            if (dist < bestD) { bestD = dist; best = d; }
        }
        if (best == null) return "nothing to hit";
        best.DamageObject(amount);
        return $"{KeyOf(best)} at {Mathf.Sqrt(bestD):0.0} m: hp {best.currentHitPoints:0.#}/{best.maxHitPoints:0.#} destroyed={best.hasBeenDestroyed} pieces={best.collectibles?.Count}";
    }
}

// Collecting is partly inlined in the game, so watch every entry point; WorldObjects reports each object once.
[HarmonyPatch]
internal static class WorldObjectHooks
{
    [HarmonyPostfix, HarmonyPatch(typeof(CollectibleID), nameof(CollectibleID.OnCollect))]
    private static void OnCollect(CollectibleID __instance) => WorldObjects.OnLocalCollected(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(CollectibleID), nameof(CollectibleID.CollectItem))]
    private static void CollectItem(CollectibleID __instance) => WorldObjects.OnLocalCollected(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(CollectibleID), nameof(CollectibleID.TryCollectItem))]
    private static void TryCollectItem(CollectibleID __instance, CollectibleID.CollectResult __result)
    {
        if (__result == CollectibleID.CollectResult.Collected) WorldObjects.OnLocalCollected(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(UseItem_Birds), nameof(UseItem_Birds.CollectItem))]
    private static void BirdCollect(CollectibleID __0) => WorldObjects.OnLocalCollected(__0);

    // Only damage that landed counts (invulnerable objects keep their hit points).
    [HarmonyPrefix, HarmonyPatch(typeof(DestructibleObject), nameof(DestructibleObject.DamageObject))]
    private static void DamageBefore(DestructibleObject __instance, out float __state)
    {
        RewardSync.Breaking++;
        __state = __instance.hasBeenDestroyed ? -1 : __instance.currentHitPoints;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(DestructibleObject), nameof(DestructibleObject.DamageObject))]
    private static void DamageAfter(DestructibleObject __instance, float __0, float __state)
    {
        RewardSync.Breaking--;
        if (__state >= 0 && __instance.hasBeenDestroyed) WorldObjects.OnDestroyed(__instance);
        if (__state >= 0 && (__instance.currentHitPoints < __state || __instance.hasBeenDestroyed)) WorldObjects.OnLocalDamage(__instance, __0);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(DestructibleObject), nameof(DestructibleObject.DestroyObject))]
    private static void Destroyed(DestructibleObject __instance) => WorldObjects.OnDestroyed(__instance);
}
