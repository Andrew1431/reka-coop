using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiteNetLib.Utils;
using UnityEngine;

namespace RekaCoop.Sync;

// The hut's house blocks, in the game's own placement descriptor (HouseBlockSpec, relative to the hut) plus the
// block's type id and placement index. The index is the block's identity: cut settings refer to other blocks by it.
public class HouseBlock
{
    public int Index;
    public string TypeId;
    public Vector3 RefPos, LocalPos, DiagonalForward, BoardDirection;
    public Quaternion RefRot, LocalRot;
    public bool IsDiagonal, KeepWhole, Reversed;
    public int[] RefusedCutters = new int[0];
    public byte Braces;
    public ushort RemovedExtras;
    public int FitTarget;

    public void Write(NetDataWriter w)
    {
        w.Put(Index); w.Put(TypeId ?? "");
        PutV(w, RefPos); PutQ(w, RefRot); PutV(w, LocalPos); PutQ(w, LocalRot); PutV(w, DiagonalForward); PutV(w, BoardDirection);
        w.Put((byte)((IsDiagonal ? 1 : 0) | (KeepWhole ? 2 : 0) | (Reversed ? 4 : 0)));
        w.Put((byte)RefusedCutters.Length);
        foreach (var c in RefusedCutters) w.Put(c);
        w.Put(Braces); w.Put(RemovedExtras); w.Put(FitTarget);
    }

    public static HouseBlock Read(NetDataReader r)
    {
        var b = new HouseBlock { Index = r.GetInt(), TypeId = r.GetString() };
        b.RefPos = GetV(r); b.RefRot = GetQ(r); b.LocalPos = GetV(r); b.LocalRot = GetQ(r); b.DiagonalForward = GetV(r); b.BoardDirection = GetV(r);
        byte flags = r.GetByte();
        b.IsDiagonal = (flags & 1) != 0; b.KeepWhole = (flags & 2) != 0; b.Reversed = (flags & 4) != 0;
        b.RefusedCutters = new int[r.GetByte()];
        for (int i = 0; i < b.RefusedCutters.Length; i++) b.RefusedCutters[i] = r.GetInt();
        b.Braces = r.GetByte(); b.RemovedExtras = r.GetUShort(); b.FitTarget = r.GetInt();
        return b;
    }

    // Same block, same place, same settings (to a millimetre / a tenth of a degree).
    public bool SameAs(HouseBlock o) =>
        o != null && Index == o.Index && TypeId == o.TypeId && Near(RefPos, o.RefPos) && Near(LocalPos, o.LocalPos)
        && Quaternion.Angle(RefRot, o.RefRot) < 0.1f && Quaternion.Angle(LocalRot, o.LocalRot) < 0.1f
        && IsDiagonal == o.IsDiagonal && KeepWhole == o.KeepWhole && Reversed == o.Reversed && Braces == o.Braces
        && RemovedExtras == o.RemovedExtras && FitTarget == o.FitTarget && RefusedCutters.SequenceEqual(o.RefusedCutters)
        && Near(DiagonalForward, o.DiagonalForward) && Near(BoardDirection, o.BoardDirection);

    // The same block (settings may differ), as opposed to another one that got the same index.
    public bool SamePlace(HouseBlock o) => o != null && TypeId == o.TypeId && Near(RefPos, o.RefPos) && Near(LocalPos, o.LocalPos);

    public override string ToString() => $"#{Index} {TypeId} part {RefPos} local {LocalPos}";

    private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;
    private static void PutV(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static void PutQ(NetDataWriter w, Quaternion q) { w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w); }
    private static Vector3 GetV(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
    private static Quaternion GetQ(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
}

public static class HouseBlocks
{
    private static BB_Building Building => RekaCoop.Sync.Find.One<BB_Building>();
    private static PossessionTypeReference Ids => RekaCoop.Sync.Find.One<SaveLoadBBs>()?.BBIDConverter;

    public static bool Ready => Building != null && Ids != null && BB_Building.Home != null && !SaveLoadBBs.isloading;

    public static Dictionary<int, HouseBlock> Capture()
    {
        var blocks = new Dictionary<int, HouseBlock>();
        var ids = Ids;
        var home = BB_Building.Home;
        if (ids == null || home == null || BB_Building.placedBBs == null) return blocks;
        foreach (var kv in BB_Building.placedBBs)
        {
            if (kv.Key == null || kv.Value == null) continue;
            var hbo = kv.Key.GetComponent<HouseBuildingObject>();
            if (hbo == null) continue;
            var spec = HouseBlockSpec.FromPlaced(hbo, kv.Value, home);
            var cut = spec.cutSettings;
            blocks[hbo.placementIndex] = new HouseBlock
            {
                Index = hbo.placementIndex, TypeId = ids.TypeToID(kv.Value),
                RefPos = spec.refPosition, RefRot = spec.refRotation, LocalPos = spec.blockLocalPosition, LocalRot = spec.blockLocalRotation,
                IsDiagonal = spec.isDiagonal, DiagonalForward = spec.diagonalForwardRef, BoardDirection = spec.boardDirectionRef,
                KeepWhole = cut.keepWhole, Reversed = cut.reversed, Braces = cut.braceOverrides, RemovedExtras = cut.removedExtras,
                RefusedCutters = cut.refusedCutters != null ? cut.refusedCutters.ToArray() : new int[0],
                FitTarget = spec.fitTarget,
            };
        }
        return blocks;
    }

    public static GameObject Find(int index)
    {
        foreach (var kv in BB_Building.placedBBs)
        {
            if (kv.Key == null) continue;
            var hbo = kv.Key.GetComponent<HouseBuildingObject>();
            if (hbo != null && hbo.placementIndex == index) return kv.Key;
        }
        return null;
    }

    public static GameObject Spawn(HouseBlock b)
    {
        var card = Ids.IDToType(b.TypeId)?.TryCast<BuildingBlock>();
        if (card == null) { Plugin.Logger.LogWarning($"Build: unknown block type {b.TypeId}"); return null; }
        var cut = new HouseCutSettings
        {
            keepWhole = b.KeepWhole, reversed = b.Reversed, braceOverrides = b.Braces, removedExtras = b.RemovedExtras,
            refusedCutters = new Il2CppStructArray<int>(b.RefusedCutters),
        };
        var spec = new HouseBlockSpec
        {
            block = card, refPosition = b.RefPos, refRotation = b.RefRot, blockLocalPosition = b.LocalPos, blockLocalRotation = b.LocalRot,
            isDiagonal = b.IsDiagonal, diagonalForwardRef = b.DiagonalForward, boardDirectionRef = b.BoardDirection,
            assignBoardDirection = b.BoardDirection != Vector3.zero, cutSettings = cut, fitTarget = b.FitTarget, placementIndex = b.Index,
        };
        Spawning = true;
        try { return Building.SpawnHouseBlockInHome(ref spec, new Pose(b.RefPos, b.RefRot), b.Index); }
        finally { Spawning = false; }
    }

    internal static bool Spawning;
    // Blocks a player places get indices in their own lane (index % Lanes == player id % Lanes), so two players building
    // at once never pick the same number. The host's renumbering stays as a fallback.
    internal const int Lanes = 8;
    internal static int? Lane;

    public static void Remove(GameObject go) => Building.RemoveHouseBlockUnrecorded(go);

    // Dev: build a copy of block <index> shifted along its part, through the game's normal commit (pays, records undo).
    public static string BuildNextTo(int index)
    {
        var all = Capture();
        if (!all.TryGetValue(index, out var b)) return $"no block {index}";
        var card = Ids.IDToType(b.TypeId)?.TryCast<BuildingBlock>();
        var sb = new StringBuilder();
        foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up, 2 * Vector3.right, 2 * Vector3.left })
        {
            var spec = new HouseBlockSpec
            {
                block = card, refPosition = b.RefPos, refRotation = b.RefRot, blockLocalPosition = b.LocalPos + offset, blockLocalRotation = b.LocalRot,
                isDiagonal = b.IsDiagonal, diagonalForwardRef = b.DiagonalForward, boardDirectionRef = b.BoardDirection,
                assignBoardDirection = b.BoardDirection != Vector3.zero, cutSettings = new HouseCutSettings { refusedCutters = new Il2CppStructArray<int>(0) },
                fitTarget = -1, placementIndex = -1,
            };
            bool ok = Building.TryCommitSpec(ref spec, true);
            sb.Append($" {offset}: {(ok ? "placed" : spec.refusal.ToString())};");
            if (ok) break;
        }
        return sb.ToString();
    }

    // Dev: remove a block the way the player does (refund included).
    public static string Unbuild(int index)
    {
        var go = Find(index);
        if (go == null) return $"no block {index}";
        Building.RemoveBBFromWorld(go);
        return $"removed {index}";
    }

    public static string Describe()
    {
        var blocks = Capture();
        ulong hash = 1469598103934665603UL;
        foreach (var x in Encoding.UTF8.GetBytes(Dump(blocks))) hash = (hash ^ x) * 1099511628211UL;
        return $"{blocks.Count} blocks, hash {hash:x16}, max index {(blocks.Count > 0 ? blocks.Keys.Max() : 0)}, ready={Ready}";
    }

    // One line per block, rounded so float noise doesn't show.
    public static string Dump(Dictionary<int, HouseBlock> blocks = null)
    {
        static string V(Vector3 v) => $"{v.x:0.000},{v.y:0.000},{v.z:0.000}";
        static string Q(Quaternion q) => V(q.eulerAngles);
        var sb = new StringBuilder();
        foreach (var b in (blocks ?? Capture()).Values.OrderBy(b => b.Index))
            sb.Append($"{b.Index} {b.TypeId} {V(b.RefPos)} {Q(b.RefRot)} {V(b.LocalPos)} {Q(b.LocalRot)} d{b.IsDiagonal} {V(b.DiagonalForward)} {V(b.BoardDirection)} " +
                      $"k{b.KeepWhole} r{b.Reversed} c[{string.Join(",", b.RefusedCutters)}] b{b.Braces} x{b.RemovedExtras} f{b.FitTarget}\n");
        foreach (var f in HouseFurniture.Capture().Values.Select(v => v.f).OrderBy(f => f.Key))
            sb.Append($"furniture {f.Key} {V(f.Rot.eulerAngles)} under #{f.ParentIndex}\n");
        return sb.ToString().Replace("-0.000", "0.000");
    }

    // Dev check that a captured block can be put back exactly: remove the newest block and spawn it from its capture.
    public static string RoundTrip()
    {
        var before = Capture();
        if (before.Count == 0) return "no blocks";
        var b = before[before.Keys.Max()];
        var go = Find(b.Index);
        Remove(go);
        var spawned = Spawn(b);
        var after = Capture();
        var sb = new StringBuilder($"round trip {b}: spawned={spawned != null} count {before.Count}->{after.Count}");
        if (after.TryGetValue(b.Index, out var a)) sb.Append(a.SameAs(b) ? " identical" : $" DIFFERS: now {a}");
        else sb.Append(" MISSING after spawn");
        return sb.ToString();
    }
}

// Furniture and stations (everything placed that isn't a house block). They have no index, so one is named by its
// type and where it stands in the hut; it hangs under a house block (by that block's index) or the hut itself.
public class HouseFurniture
{
    public string TypeId, ParentPath;
    public int ParentIndex;          // 0 = under the hut itself
    public Vector3 Pos;              // relative to the hut
    public Quaternion Rot;

    public string Key => $"{TypeId}@{Mathf.RoundToInt(Pos.x * 100)},{Mathf.RoundToInt(Pos.y * 100)},{Mathf.RoundToInt(Pos.z * 100)}";

    public void Write(NetDataWriter w)
    {
        w.Put(TypeId ?? ""); w.Put(ParentPath ?? ""); w.Put(ParentIndex);
        w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z); w.Put(Rot.x); w.Put(Rot.y); w.Put(Rot.z); w.Put(Rot.w);
    }

    public static HouseFurniture Read(NetDataReader r) => new()
    {
        TypeId = r.GetString(), ParentPath = r.GetString(), ParentIndex = r.GetInt(),
        Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
    };

    public bool SameAs(HouseFurniture o) => o != null && Key == o.Key && Quaternion.Angle(Rot, o.Rot) < 0.5f && ParentIndex == o.ParentIndex;

    public override string ToString() => Key;

    private static BB_Building Building => RekaCoop.Sync.Find.One<BB_Building>();
    private static PossessionTypeReference Ids => RekaCoop.Sync.Find.One<SaveLoadBBs>()?.BBIDConverter;

    public static Dictionary<string, (HouseFurniture f, GameObject go)> Capture()
    {
        var all = new Dictionary<string, (HouseFurniture, GameObject)>();
        var ids = Ids;
        var home = BB_Building.Home;
        if (ids == null || home == null || BB_Building.placedBBs == null) return all;
        foreach (var kv in BB_Building.placedBBs)
        {
            if (kv.Key == null || kv.Value == null || kv.Key.GetComponent<HouseBuildingObject>() != null) continue;
            var t = kv.Key.transform;
            var f = new HouseFurniture
            {
                TypeId = ids.TypeToID(kv.Value),
                Pos = home.InverseTransformPoint(t.position), Rot = Quaternion.Inverse(home.rotation) * t.rotation,
            };
            Transform anchor = home;
            for (var p = t.parent; p != null && p != home; p = p.parent)
            {
                var hbo = p.GetComponent<HouseBuildingObject>();
                if (hbo != null) { f.ParentIndex = hbo.placementIndex; anchor = p; break; }
            }
            f.ParentPath = t.parent == null ? "" : GearObjects.RelativePath(anchor, t.parent);
            all[f.Key] = (f, kv.Key);
        }
        return all;
    }

    public GameObject Spawn()
    {
        var card = Ids.IDToType(TypeId)?.TryCast<BuildingBlock>();
        if (card == null) { Plugin.Logger.LogWarning($"Build: unknown furniture type {TypeId}"); return null; }
        var home = BB_Building.Home;
        Transform anchor = ParentIndex > 0 ? HouseBlocks.Find(ParentIndex)?.transform : home;
        if (anchor == null) anchor = home;
        // Generated sub-part names carry instance ids that differ per machine; go as deep as the path matches.
        var parent = anchor;
        if (!string.IsNullOrEmpty(ParentPath))
            foreach (var name in ParentPath.Split('/'))
            {
                var next = parent.Find(name);
                if (next == null) break;
                parent = next;
            }
        return SpawnLikeLoad(card, home.TransformPoint(Pos), home.rotation * Rot, parent);
    }

    // SpawnBB only works for the card selected in build mode; loading a save instantiates and finishes directly.
    private static GameObject SpawnLikeLoad(BuildingBlock card, Vector3 pos, Quaternion rot, Transform parent)
    {
        var building = Building;
        var go = building.InstantiateBB(card, pos, rot, parent);
        if (go != null) building.FinishPlacedBB(go);
        return go;
    }

    // Without the refund the player would get for picking it up: that went to whoever removed it.
    public static void Remove(GameObject go)
    {
        var building = Building;
        var bb = building.UnregisterRemovedBB(go);
        building.TearDownRemovedBB(go, bb);
    }

    // Dev: place a piece of furniture of this type on the floor next to the newest floor block.
    public static string PlaceTest(string typeId)
    {
        var card = Ids.IDToType(typeId)?.TryCast<BuildingBlock>();
        if (card == null) return $"unknown type {typeId}";
        GameObject floor = null;
        int best = -1;
        foreach (var kv in BB_Building.placedBBs)
        {
            var hbo = kv.Key == null ? null : kv.Key.GetComponent<HouseBuildingObject>();
            if (hbo != null && Ids.TypeToID(kv.Value).StartsWith("HouseBB_Floor") && hbo.placementIndex > best) { best = hbo.placementIndex; floor = kv.Key; }
        }
        if (floor == null) return "no floor";
        var pos = floor.transform.position + Vector3.up * 0.15f;
        var go = SpawnLikeLoad(card, pos, BB_Building.Home.rotation, BB_Building.FindChildContainer(floor.transform) ?? floor.transform);
        return go == null ? "spawn failed" : $"placed {typeId} on floor #{best} under {GearObjects.PathOf(go.transform.parent)}";
    }
}

// Building together. Each machine compares its house with the last agreed one a few times a second; whatever changed
// locally (placed, removed, swapped, cut, undone - every path ends in the placed set) is sent as puts and removes.
// The host applies a guest's changes, relays them, and sends its whole house to a guest that has just loaded (the
// guest's copy came from the host's last save, which may be older). Applying is the game's own load path: a block is
// spawned from its spec with its placement index, furniture with SpawnBB; it costs nothing and touches no undo history.
public static class BuildSync
{
    private enum Kind : byte { Changes = 1, Ready = 2, Snapshot = 3 }

    private class Changes
    {
        public List<int> Removed = new();
        public List<HouseBlock> Put = new();
        public List<string> FurnitureRemoved = new();
        public List<HouseFurniture> FurniturePut = new();
        public bool Any => Removed.Count + Put.Count + FurnitureRemoved.Count + FurniturePut.Count > 0;
        public override string ToString() =>
            $"{Put.Count} blocks placed/changed, {Removed.Count} removed, {FurniturePut.Count} furniture placed, {FurnitureRemoved.Count} removed " +
            $"({string.Join(", ", Put.Take(2).Select(b => b.ToString()).Concat(FurniturePut.Take(2).Select(f => f.Key)))})";

        public void Write(NetDataWriter w)
        {
            w.Put(Removed.Count); foreach (var i in Removed) w.Put(i);
            w.Put(Put.Count); foreach (var b in Put) b.Write(w);
            w.Put(FurnitureRemoved.Count); foreach (var k in FurnitureRemoved) w.Put(k);
            w.Put(FurniturePut.Count); foreach (var f in FurniturePut) f.Write(w);
        }

        public static Changes Read(NetDataReader r)
        {
            var c = new Changes();
            int n = r.GetInt(); for (int i = 0; i < n; i++) c.Removed.Add(r.GetInt());
            n = r.GetInt(); for (int i = 0; i < n; i++) c.Put.Add(HouseBlock.Read(r));
            n = r.GetInt(); for (int i = 0; i < n; i++) c.FurnitureRemoved.Add(r.GetString());
            n = r.GetInt(); for (int i = 0; i < n; i++) c.FurniturePut.Add(HouseFurniture.Read(r));
            return c;
        }
    }

    // Null until this machine has a house everyone agrees on.
    private static Dictionary<int, HouseBlock> _known;
    private static Dictionary<string, HouseFurniture> _knownFurniture;
    private static float _nextPoll, _stableSince = -1;
    private static int _lastCount = -1;
    private static bool _readySent;
    private static readonly HashSet<int> WaitingGuests = new();  // host: guests that are ready before the host's house is

    public static void Attach(Net.CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == Net.MsgType.Build) OnMessage(session, from, r); };
        session.PlayerLeft += id => WaitingGuests.Remove(id);
    }

    public static void ResetLocal()
    {
        _known = null;
        _knownFurniture = null;
        _stableSince = -1;
        _lastCount = -1;
        _readySent = false;
    }

    public static void Tick(Net.CoopSession session)
    {
        HouseBlocks.Lane = session.IsConnected ? session.LocalPlayerId % HouseBlocks.Lanes : null;
        if (!session.IsConnected || !HouseBlocks.Ready || Time.unscaledTime < _nextPoll) return;
        var building = RekaCoop.Sync.Find.One<BB_Building>();
        _nextPoll = Time.unscaledTime + (building != null && building.buildModeEnabled ? 0.3f : 5f);  // outside build mode only remote edits change it

        if (_known == null)
        {
            // Wait for the loaded house to settle before taking it as the starting point.
            int count = BB_Building.placedBBs.Count;
            if (count != _lastCount) { _lastCount = count; _stableSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _stableSince < 2f) return;
            if (session.Role == Net.Role.Host)
            {
                TakeAsAgreed();
                Plugin.Logger.LogInfo($"Build: house ready, {HouseBlocks.Describe()}, {_knownFurniture.Count} furniture");
                foreach (var id in WaitingGuests) SendSnapshot(session, id);
                WaitingGuests.Clear();
            }
            else if (!_readySent)
            {
                _readySent = true;
                var w = session.BeginMessage(Net.MsgType.Build);
                w.Put((byte)Kind.Ready);
                session.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            return;
        }

        var c = Diff(_known, _knownFurniture);
        if (!c.Any) return;
        TakeAsAgreed();
        var m = session.BeginMessage(Net.MsgType.Build);
        m.Put((byte)Kind.Changes);
        c.Write(m);
        session.Send(m, LiteNetLib.DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Build: sent {c}");
    }

    // What it takes to turn the given house into this machine's current one.
    private static Changes Diff(Dictionary<int, HouseBlock> from, Dictionary<string, HouseFurniture> fromFurniture)
    {
        var now = HouseBlocks.Capture();
        var nowFurniture = HouseFurniture.Capture();
        return new Changes
        {
            Put = now.Values.Where(b => !from.TryGetValue(b.Index, out var k) || !k.SameAs(b)).ToList(),
            Removed = from.Keys.Where(i => !now.ContainsKey(i)).ToList(),
            FurniturePut = nowFurniture.Values.Select(v => v.f).Where(f => !fromFurniture.TryGetValue(f.Key, out var k) || !k.SameAs(f)).ToList(),
            FurnitureRemoved = fromFurniture.Keys.Where(k => !nowFurniture.ContainsKey(k)).ToList(),
        };
    }

    private static void TakeAsAgreed()
    {
        _known = HouseBlocks.Capture();
        _knownFurniture = new Dictionary<string, HouseFurniture>();
        foreach (var kv in HouseFurniture.Capture()) _knownFurniture[kv.Key] = kv.Value.f;
    }

    private static void OnMessage(Net.CoopSession session, Net.RemotePlayer from, LiteNetLib.Utils.NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Net.Role.Host)
        {
            if (_known == null) WaitingGuests.Add(from.PlayerId);
            else SendSnapshot(session, from.PlayerId);
        }
        else if (kind == Kind.Snapshot)
        {
            // The host's whole house; apply only what differs from this one.
            var target = Changes.Read(r);
            var targetBlocks = target.Put.ToDictionary(b => b.Index);
            var targetFurniture = new Dictionary<string, HouseFurniture>();
            foreach (var f in target.FurniturePut) targetFurniture[f.Key] = f;
            var mine = Diff(targetBlocks, targetFurniture);  // what this machine has that the host doesn't (or has differently)
            var c = new Changes
            {
                Removed = mine.Put.Where(b => !targetBlocks.ContainsKey(b.Index)).Select(b => b.Index).ToList(),
                Put = mine.Put.Where(b => targetBlocks.ContainsKey(b.Index)).Select(b => targetBlocks[b.Index]).Concat(mine.Removed.Select(i => targetBlocks[i])).ToList(),
                FurnitureRemoved = mine.FurniturePut.Where(f => !targetFurniture.ContainsKey(f.Key)).Select(f => f.Key).ToList(),
                FurniturePut = mine.FurniturePut.Where(f => targetFurniture.ContainsKey(f.Key)).Select(f => targetFurniture[f.Key]).Concat(mine.FurnitureRemoved.Select(k => targetFurniture[k])).ToList(),
            };
            Apply(c);
            TakeAsAgreed();
            Plugin.Logger.LogInfo($"Build: took the host's house: {c}; now {HouseBlocks.Describe()}, {_knownFurniture.Count} furniture");
        }
        else if (kind == Kind.Changes)
        {
            var c = Changes.Read(r);
            if (session.Role == Net.Role.Host) Arbitrate(session, from, c);
            if (session.Role == Net.Role.Host)
            {
                var w = session.BeginMessage(Net.MsgType.Build);
                w.Put((byte)Kind.Changes);
                c.Write(w);
                session.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
            }
            if (_known == null) return;  // the snapshot will carry it
            Apply(c);
            // Take the result as agreed, so it isn't sent back; anything else that changed meanwhile still is.
            var now = HouseBlocks.Capture();
            foreach (var i in c.Removed) _known.Remove(i);
            foreach (var b in c.Put) { if (now.TryGetValue(b.Index, out var k)) _known[b.Index] = k; else _known.Remove(b.Index); }
            var nowFurniture = HouseFurniture.Capture();
            foreach (var k in c.FurnitureRemoved) _knownFurniture.Remove(k);
            foreach (var f in c.FurniturePut) { if (nowFurniture.TryGetValue(f.Key, out var k)) _knownFurniture[f.Key] = k.f; else _knownFurniture.Remove(f.Key); }
            Plugin.Logger.LogInfo($"Build: player {from.PlayerId}'s changes: {c}");
        }
    }

    // Host: two players placed different blocks in the same instant and both got the next index. The host's block (or
    // the one that reached the host first) keeps it; the newcomer moves to a free index, and its sender is told both.
    private static void Arbitrate(Net.CoopSession session, Net.RemotePlayer from, Changes c)
    {
        if (_known == null) return;
        var now = HouseBlocks.Capture();
        var fix = new Changes();
        int next = System.Math.Max(BB_Building.HighestPlacementIndex(), now.Count > 0 ? now.Keys.Max() : 0) + 1;
        foreach (var b in c.Put)
        {
            if (!now.TryGetValue(b.Index, out var mine) || mine.SamePlace(b)) continue;
            Plugin.Logger.LogInfo($"Build: player {from.PlayerId}'s {b} collides with {mine}; it becomes #{next}");
            b.Index = next++;
            fix.Put.Add(mine);
            fix.Put.Add(b);
        }
        if (!fix.Any) return;
        var w = session.BeginMessage(Net.MsgType.Build);
        w.Put((byte)Kind.Changes);
        fix.Write(w);
        session.SendTo(from.PlayerId, w, LiteNetLib.DeliveryMethod.ReliableOrdered);
    }

    private static void SendSnapshot(Net.CoopSession session, int playerId)
    {
        var w = session.BeginMessage(Net.MsgType.Build);
        w.Put((byte)Kind.Snapshot);
        new Changes { Put = _known.Values.ToList(), FurniturePut = _knownFurniture.Values.ToList() }.Write(w);
        session.SendTo(playerId, w, LiteNetLib.DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Build: sent the house ({_known.Count} blocks, {_knownFurniture.Count} furniture) to player {playerId}");
    }

    private static void Apply(Changes c)
    {
        var furniture = HouseFurniture.Capture();
        foreach (var k in c.FurnitureRemoved.Concat(c.FurniturePut.Select(f => f.Key)))
            if (furniture.TryGetValue(k, out var v)) { HouseFurniture.Remove(v.go); furniture.Remove(k); }
        foreach (var i in c.Removed)
        {
            var go = HouseBlocks.Find(i);
            if (go != null) HouseBlocks.Remove(go);
        }
        foreach (var b in c.Put.OrderBy(b => b.Index))
        {
            var go = HouseBlocks.Find(b.Index);
            if (go != null) HouseBlocks.Remove(go);
            if (HouseBlocks.Spawn(b) == null) Plugin.Logger.LogWarning($"Build: could not place {b}");
        }
        foreach (var f in c.FurniturePut)
            if (f.Spawn() == null) Plugin.Logger.LogWarning($"Build: could not place {f}");
    }
}

[HarmonyLib.HarmonyPatch(typeof(HouseBBPlacement), nameof(HouseBBPlacement.AssignPlacementIndex))]
internal static class PlacementLanes
{
    private static void Postfix(HouseBuildingObject hbo)
    {
        if (HouseBlocks.Spawning || HouseBlocks.Lane is not { } lane || hbo == null) return;
        int i = hbo.placementIndex;
        while (i % HouseBlocks.Lanes != lane) i++;
        if (i == hbo.placementIndex) return;
        hbo.placementIndex = i;
        HouseBBPlacement.s_lastPlacementIndex = i;
    }
}
