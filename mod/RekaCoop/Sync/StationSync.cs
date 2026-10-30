using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The hut's working things (cauldron and workshop queues, the mill, the candle maker, garden plants, ivy, lamps and
// other on/off pieces) keep their state in the save's house section. Each one is captured the way the game saves it:
// asked to write itself into an empty save, which is then turned into XML with the game's own serializer. Its object
// ID (different on every machine) is swapped for a placeholder, so a piece on one machine can be loaded into the same
// piece on another, again through the game's own load. Running timers are left out of the comparison, so only real
// changes (a recipe queued, a plant watered, a lamp lit) are sent. Last write wins; the host relays and snapshots.
public static class StationSync
{
    private enum Kind : byte { State = 1, Ready = 2, Snapshot = 3 }

    private class Piece
    {
        public string Key;
        public Action<SaveGame> Save, Load;
        public Func<bool> Alive;
        public Func<Vector3> At;
        public float CheckedAt = float.MinValue;
    }

    private class Pending { public string Key, Xml; public float Since; }

    private const string IdToken = "@ID@";
    private static readonly Regex IdRx = new(@"<gameObjectID>(-?\d+)</gameObjectID>");
    private static readonly Regex TimerRx = new(@"<(time\w*|_lastGrowthTickTime)>[^<]*</\1>");
    private static readonly Regex WaterRx = new(@"<waterLevel>([^<]*)</waterLevel>");

    private static Dictionary<string, string> _known;   // key -> signature, as agreed
    private static readonly List<Pending> Waiting = new();
    private static float _nextPoll;
    private static bool _readySent;
    internal static bool Applying;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Station) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _known = null;
        Waiting.Clear();
        _readySent = false;
        _all = null;
        _building = null;
    }

    // Hut: only under the hut (much cheaper than searching the scene). World: anywhere (wild plants are scene roots).
    // Single: the hut has one, and it isn't under the hut's transform (so it moves with it in the world).
    private enum Where { Hut, World, Single }

    private static IEnumerable<Piece> Of<T>(Transform home, Where where) where T : MonoBehaviour
    {
        var save = typeof(T).GetMethod("ISaveable_GetStateForSaving");
        var load = typeof(T).GetMethod("ISaveable_RestoreSavedState");
        var counts = new Dictionary<string, int>();
        bool single = where == Where.Single;
        IEnumerable<T> found = where switch
        {
            Where.Hut => home.GetComponentsInChildren<T>(true),
            Where.World => (IEnumerable<T>)Find.Scanned<T>(30f, inactive: true) ?? new T[0],
            // Not Find.One: when there is none, its scene search (~35 ms) would run on every rebuild.
            _ => Find.Scanned<T>(30f) is { Length: > 0 } some ? new[] { some[0] } : new T[0],
        };
        foreach (var c in found)
        {
            if (c == null) continue;
            if (!c.gameObject.scene.IsValid()) continue;  // a prefab, not a placed piece
            bool inHut = c.transform.IsChildOf(home);
            var p = inHut ? home.InverseTransformPoint(c.transform.position) * 10 : c.transform.position;
            var key = single ? typeof(T).Name
                : $"{typeof(T).Name}:{c.name}@{(inHut ? "H" : "W")}{Mathf.RoundToInt(p.x * 10)},{Mathf.RoundToInt(p.y * 10)},{Mathf.RoundToInt(p.z * 10)}";
            int n = counts.GetValueOrDefault(key);
            counts[key] = n + 1;
            if (n > 0) key += $"#{n}";
            var target = c;
            yield return new Piece
            {
                Key = key, Save = sg => save.Invoke(target, new object[] { sg }), Load = sg => load.Invoke(target, new object[] { sg }),
                Alive = () => target != null, At = () => target.transform.position,
            };
        }
    }

    private static Dictionary<string, Piece> _all;
    private static List<Piece> _order = new();
    private static float _allAt;
    private static int _cursor;
    private const float PassSeconds = 0.5f, FarSeconds = 5f, NearRange = 25f;
    private static IEnumerator<Piece> _building;
    private static Dictionary<string, Piece> _next;

    private static IEnumerable<Piece> Pieces(Transform home) =>
        Of<SavableBBState>(home, Where.Hut).Concat(Of<SavableBBMultiState>(home, Where.Hut))
            .Concat(Of<PlantGrowth>(home, Where.World)).Concat(Of<IvyGrowth>(home, Where.World))
            .Concat(Of<BB_Logic_Workshop>(home, Where.Hut)).Concat(Of<BB_Logic_Mill>(home, Where.Hut)).Concat(Of<BB_CandleMakerLogic>(home, Where.Hut))
            .Concat(Of<BB_Logic_RitualDesk>(home, Where.Hut)).Concat(Of<BB_Logic_FairyGarden>(home, Where.Hut))
            .Concat(Of<BB_Logic_DeerFeedingPlace>(home, Where.Hut)).Concat(Of<FeatherColorController>(home, Where.Single));

    // Looked up again every 30 s, a few pieces a frame (finding and naming them all at once is a visible hitch); the
    // previous list stays in use meanwhile.
    private static void Rebuild(double budgetMs)
    {
        if (_building == null)
        {
            if (_all != null && Time.unscaledTime - _allAt < 30f) return;
            var home = BB_Building.Home;
            if (home == null) { _all = new Dictionary<string, Piece>(); _allAt = Time.unscaledTime; return; }
            _building = Pieces(home).GetEnumerator();
            _next = new Dictionary<string, Piece>();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < budgetMs)
        {
            bool more;
            try { more = _building.MoveNext(); }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Stations: can't list the hut's pieces: {e.Message}");
                _building = null;
                _all ??= new Dictionary<string, Piece>();
                _allAt = Time.unscaledTime;
                return;
            }
            if (!more)
            {
                _all = _next;
                _order = _next.Values.ToList();
                _allAt = Time.unscaledTime;
                _building = null;
                return;
            }
            _next[_building.Current.Key] = _building.Current;
        }
    }

    private static bool Scanned() =>
        Find.Scanned<PlantGrowth>(30f, inactive: true) != null && Find.Scanned<IvyGrowth>(30f, inactive: true) != null;

    private static Dictionary<string, Piece> All()
    {
        if (_all == null) Rebuild(double.MaxValue);
        return _all;
    }

    // The piece's own entry in an otherwise empty house save, with its ID replaced (pieces of the hut itself, like its
    // feather colour, have none); null if it can't be captured.
    private static string Capture(Piece p, out string localId)
    {
        localId = null;
        if (!p.Alive()) return null;
        try
        {
            var sg = new SaveGame { savedBBs = new SaveLoadBBs.SavedBBs() };
            p.Save(sg);
            var xml = ClassicElementStateBridge.Serialize(Il2CppType.Of<SaveLoadBBs.SavedBBs>(), sg.savedBBs);
            if (xml == null) return null;
            var m = IdRx.Match(xml);
            if (!m.Success) return xml;
            localId = m.Groups[1].Value;
            return xml.Replace($"<gameObjectID>{localId}</gameObjectID>", $"<gameObjectID>{IdToken}</gameObjectID>");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Stations: can't capture {p.Key}: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
    }

    private static string Signature(string xml) => xml == null ? "" : WaterRx.Replace(TimerRx.Replace(xml, ""), RoundWater);

    private static string RoundWater(Match m) =>
        float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            ? $"<waterLevel>{Mathf.Round(w * 4) / 4}</waterLevel>" : m.Value;

    private static bool Apply(Piece p, string xml)
    {
        if (Capture(p, out var localId) == null || (localId == null && xml.Contains(IdToken))) return false;  // not set up yet
        try
        {
            var saved = ClassicElementStateBridge.Deserialize(Il2CppType.Of<SaveLoadBBs.SavedBBs>(), localId == null ? xml : xml.Replace(IdToken, localId))
                .Cast<SaveLoadBBs.SavedBBs>();
            saved.loadedObjectsIDLookUp = new Il2CppSystem.Collections.Generic.List<SaveLoadBBs.IDPairing>();
            if (localId != null) saved.loadedObjectsIDLookUp.Add(new SaveLoadBBs.IDPairing(int.Parse(localId), int.Parse(localId)));
            Applying = true;
            try { p.Load(new SaveGame { savedBBs = saved }); }
            finally { Applying = false; }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Stations: can't load {p.Key}: {e.InnerException?.Message ?? e.Message}");
        }
        if (_known != null) _known[p.Key] = Signature(Capture(p, out _));
        return true;
    }

    // The key, or else the same kind of piece within a few centimetres: a piece in the hut can sit a centimetre higher
    // on one copy than the other (seen: the bread oven, whose state was then dropped on both sides).
    private static bool Lookup(Dictionary<string, Piece> all, string key, out Piece piece)
    {
        if (all.TryGetValue(key, out piece)) return true;
        if (!Parse(key, out var head, out var pos, out var tail)) return false;
        float best = 4f;  // key units: centimetres in the hut, decimetres in the world
        foreach (var (k, p) in all)
        {
            if (!Parse(k, out var h, out var q, out var t) || h != head || t != tail) continue;
            float d = (q - pos).magnitude;
            if (d <= best) { best = d; piece = p; }
        }
        return piece != null;
    }

    // "Type:name@H79,507,-348#1" -> ("Type:name@H", (79, 507, -348), "#1")
    private static bool Parse(string key, out string head, out Vector3 pos, out string tail)
    {
        head = tail = null; pos = default;
        int at = key.LastIndexOf('@');
        if (at < 0 || at + 1 >= key.Length) return false;
        head = key[..(at + 2)];
        var rest = key[(at + 2)..];
        int hash = rest.IndexOf('#');
        tail = hash < 0 ? "" : rest[hash..];
        var nums = (hash < 0 ? rest : rest[..hash]).Split(',');
        if (nums.Length != 3 || !int.TryParse(nums[0], out var x) || !int.TryParse(nums[1], out var y) || !int.TryParse(nums[2], out var z)) return false;
        pos = new Vector3(x, y, z);
        return true;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || !HouseBlocks.Ready || !Scanned()) return;
        Rebuild(1.0);
        if (_all == null) return;
        var all = _all;

        if (_known == null)
        {
            if (session.Role == Role.Host) { _known = Signatures(all); return; }
            if (_readySent) return;
            _readySent = true;
            var rw = session.BeginMessage(MsgType.Station);
            rw.Put((byte)Kind.Ready);
            session.Send(rw, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (Time.unscaledTime >= _nextPoll)
        {
            _nextPoll = Time.unscaledTime + 1f;
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                var w = Waiting[i];
                if (Lookup(all, w.Key, out var p) && Apply(p, w.Xml)) Waiting.RemoveAt(i);
                else if (Time.unscaledTime - w.Since > 20f) { Plugin.Logger.LogWarning($"Stations: no {w.Key} here; dropped its state"); Waiting.RemoveAt(i); }
            }
        }

        // Pieces near a player twice a second, others every few seconds (each capture is an XML write, and the garbage
        // made the game's collector stall the frame), two a frame at most.
        int captures = 0;
        for (int tries = 0; captures < 2 && tries < _order.Count && tries < 64; tries++)
        {
            if (_cursor >= _order.Count) _cursor = 0;
            var p = _order[_cursor++];
            float age = Time.unscaledTime - p.CheckedAt;
            if (age < PassSeconds || (age < FarSeconds && !(p.Alive() && PlayerSync.NearAnyPlayer(p.At(), NearRange)))) continue;
            p.CheckedAt = Time.unscaledTime;
            captures++;
            var key = p.Key;
            var xml = Capture(p, out _);
            if (xml == null) continue;
            var sig = Signature(xml);
            if (_known.TryGetValue(key, out var k) && k == sig) continue;
            bool isNew = !_known.ContainsKey(key);
            _known[key] = sig;
            if (isNew) continue;  // just built or just set up: not a change
            var w = session.BeginMessage(MsgType.Station);
            w.Put((byte)Kind.State);
            w.Put(key);
            w.Put(xml);
            session.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Stations: {key} changed ({xml.Length} chars)");
        }
    }

    private static Dictionary<string, string> Signatures(Dictionary<string, Piece> all)
    {
        var d = new Dictionary<string, string>();
        foreach (var (key, p) in all)
        {
            var xml = Capture(p, out _);
            if (xml != null) d[key] = Signature(xml);
        }
        return d;
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var w = session.BeginMessage(MsgType.Station);
            w.Put((byte)Kind.Snapshot);
            var states = All().Select(kv => (kv.Key, xml: Capture(kv.Value, out _))).Where(s => s.xml != null).ToList();
            w.Put(states.Count);
            foreach (var (key, xml) in states) { w.Put(key); w.Put(xml); }
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Stations: sent {states.Count} hut pieces to player {from.PlayerId}");
        }
        else if (kind == Kind.Snapshot)
        {
            int n = r.GetInt(), changed = 0;
            var all = All();
            _known = Signatures(all);
            for (int i = 0; i < n; i++)
            {
                string key = r.GetString(), xml = r.GetString();
                if (_known.TryGetValue(key, out var k) && k == Signature(xml)) continue;
                changed++;
                if (!Lookup(all, key, out var p) || !Apply(p, xml)) Waiting.Add(new Pending { Key = key, Xml = xml, Since = Time.unscaledTime });
            }
            Plugin.Logger.LogInfo($"Stations: host's {n} hut pieces, {changed} differed here");
        }
        else if (kind == Kind.State)
        {
            string key = r.GetString(), xml = r.GetString();
            if (session.Role == Role.Host)
            {
                var w = session.BeginMessage(MsgType.Station);
                w.Put((byte)Kind.State);
                w.Put(key);
                w.Put(xml);
                session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
            }
            if (_known == null) return;  // the snapshot will carry it
            if (!Lookup(All(), key, out var p) || !Apply(p, xml)) Waiting.Add(new Pending { Key = key, Xml = xml, Since = Time.unscaledTime });
            Plugin.Logger.LogInfo($"Stations: player {from.PlayerId} changed {key}");
        }
    }

    internal static string Bench()
    {
        var home = BB_Building.Home;
        var sb = new System.Text.StringBuilder("stations:");
        void T<X>(Where w) where X : MonoBehaviour
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = Of<X>(home, w).Count();
            sb.Append($" {typeof(X).Name} {n} {sw.Elapsed.TotalMilliseconds:0.00};");
        }
        T<SavableBBState>(Where.Hut); T<SavableBBMultiState>(Where.Hut); T<PlantGrowth>(Where.World); T<IvyGrowth>(Where.World);
        T<BB_Logic_Workshop>(Where.Hut); T<BB_Logic_Mill>(Where.Hut); T<BB_CandleMakerLogic>(Where.Hut); T<BB_Logic_RitualDesk>(Where.Hut);
        T<BB_Logic_FairyGarden>(Where.Hut); T<BB_Logic_DeerFeedingPlace>(Where.Hut); T<FeatherColorController>(Where.Single);
        var c = System.Diagnostics.Stopwatch.StartNew();
        foreach (var p in All().Values) Capture(p, out _);
        sb.Append($" capture all {All().Count} {c.Elapsed.TotalMilliseconds:0.00}");
        return sb.ToString();
    }

    public static string DescribeAll() =>
        string.Join("\n", All().Select(kv => $"{kv.Key}: {Signature(Capture(kv.Value, out var id))?.Length} chars, id {id}"));

    public static string Dump(string filter)
    {
        var p = All().FirstOrDefault(kv => kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).Value;
        return p == null ? "none" : $"{p.Key}\n{Capture(p, out _)}";
    }
}
