using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Places on the map (villages, shrines, the trial site, encounters) save their state themselves: which parts are
// switched on, solved stations, requests filled, and so on. Each place has an ID that is the same on every machine
// (it comes from the save), so a place's whole state is captured the way the game saves it, compared, and loaded into
// the same place elsewhere. Positions of things that move by themselves (animals, villagers) are left out of the
// comparison (and so is which of a place's parts are showing, which villagers' tools flicker); element states,
// stations and counters count. Places are checked a few at a time, by their parts alone (cheap); only a place whose
// parts changed is captured whole.
public static class PoiSync
{
    private enum Kind : byte { State = 1, Ready = 2, Snapshot = 3, Request = 4 }

    // A guest only reports changes to places near it; a far place that changed by itself there (a villager's request
    // rolled differently, say) is fetched from the host instead.
    private const float GuestReach = 40f;

    private class Pending { public string Key; public byte[] Gz; public float Since; }

    private static readonly Regex FloatRx = new(@">-?\d+\.\d+(E[-+]?\d+)?<");
    private static readonly Regex AxisRx = new(@"<([xyzw])>[^<]*</\1>|<setActiveOrder>.*?</setActiveOrder>|<setActiveOrder />|<debug>.*?</debug>", RegexOptions.Singleline);
    // Where each child sits and whether it shows; villagers' tools and animals flicker these all the time.
    // what animals and villagers are busy with (their own AI, not the place's state), and countdowns.
    private static readonly Regex TransformRx = new(@"<SerializableTransformActiveWithID>.*?</SerializableTransformActiveWithID>|<SerializableAIController>.*?</SerializableAIController>"
        + @"|<SerializableIdIdIntPairing><id>[^<]*</id><id2>[^<]*pendingRunMs[^<]*</id2><value>[^<]*</value></SerializableIdIdIntPairing>",
        RegexOptions.Singleline);

    private static Dictionary<string, string> _known;          // uniqueID -> signature, as agreed
    private static readonly Dictionary<string, string> KnownParts = new();  // uniqueID -> signature of its parts, as last checked
    private static readonly Dictionary<string, float> NextCheck = new();
    private static readonly List<Pending> Waiting = new();
    private static List<POI_SaveLoad> _queue = new();
    private static int _cursor;
    private static float _nextStep, _nextRefresh;
    private static readonly Dictionary<string, float> CheckedAt = new();
    private const float FarSeconds = 10f, NearRange = 80f;  // from the place's root; towns are wide
    private static bool _readySent;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Poi) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _known = null;
        KnownParts.Clear();
        NextCheck.Clear();
        CheckedAt.Clear();
        Waiting.Clear();
        _queue.Clear();
        _readySent = false;
    }

    private static Dictionary<string, POI_SaveLoad> All()
    {
        var all = new Dictionary<string, POI_SaveLoad>();
        foreach (var p in (IEnumerable<POI_SaveLoad>)RekaCoop.Sync.Find.Scanned<POI_SaveLoad>(5f) ?? UnityEngine.Object.FindObjectsByType<POI_SaveLoad>(FindObjectsSortMode.None))
            if (p.isInited && !string.IsNullOrEmpty(p.uniqueID)) all[p.uniqueID] = p;
        return all;
    }

    private static string Capture(POI_SaveLoad p, out double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (p == null) { ms = 0; return null; }  // gone with the old map (a guest's request arriving during travel)
        try
        {
            var map = new SaveableMap();
            p.ISaveableMapElement_GetStateForSaving(map);
            return ClassicElementStateBridge.Serialize(Il2CppType.Of<SaveableMap>(), map);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Places: can't capture {p.name}: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
        finally { ms = sw.Elapsed.TotalMilliseconds; }
    }

    // Only the place's parts (stations, requests, counters), without the thousands of child transforms a big place
    // also saves (the trial site: ~2000, 56 ms to capture whole). Cheap enough to watch big places for changes;
    // only a change pays for the full capture.
    internal static string CaptureParts(POI_SaveLoad p, out double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (p == null) { ms = 0; return null; }  // gone with the old map (a guest's request arriving during travel)
        try
        {
            var map = new SaveableMap();
            var parts = p.poiElementsToSave;
            if (parts != null)
                foreach (var e in parts)
                    e?.poiElement?.GetStateForSaving(map, p.uniqueID, e.id);
            if (p.saveElementStates) p._elementStates?.Capture(map, p.uniqueID, p.transform);
            return ClassicElementStateBridge.Serialize(Il2CppType.Of<SaveableMap>(), map);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Places: can't capture {p.name}'s parts: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
        finally { ms = sw.Elapsed.TotalMilliseconds; }
    }

    // Dev: what the parts capture costs and whether it carries everything the full one compares.
    public static string PartsTest(string name)
    {
        var sb = new StringBuilder();
        foreach (var p in All().Values)
        {
            if (name != null && !p.name.Contains(name)) continue;
            var full = Capture(p, out var fms);
            var parts = CaptureParts(p, out var pms);
            if (full == null || parts == null) continue;
            string fs = Signature(full), ps = Signature(parts);
            sb.Append($"{p.name}: full {full.Length}/{fms:0.0}ms parts {parts.Length}/{pms:0.0}ms sig {(fs == ps ? "same" : $"differs {FirstDifference(fs, ps)}")}; ");
        }
        return sb.ToString();
    }

    // What an animal or villager's controller is busy with (Baba Jaga walking between the ritual's candles): it changed
    // every few seconds, and each change sent the whole place, which put back someone's half-burnt candle ring.
    private static readonly Regex AiStateRx = new(@"(<debugName>GeneralAIController</debugName><flags>)[^<]*(</flags><ints>).*?(</ints>)", RegexOptions.Singleline);

    private static string Signature(string xml) => xml == null ? "" : FloatRx.Replace(AxisRx.Replace(TransformRx.Replace(AiStateRx.Replace(xml, "$1$2$3"), ""), ""), "><");

    // Use-an-item areas (the ritual's candle rings, burnt with the pine branch) only fill up. Two players burning
    // different rings each sent a whole place with only their own ring filled; a state that arrives keeps what is
    // filled here.
    private static readonly Regex AreaRx = new(@"(<uniquePoiElementID>([^<]*)</uniquePoiElementID><uniquePoiID>[^<]*</uniquePoiID><debugName>UseItemInteractArea</debugName><flags>[^<]*</flags><ints>)(.*?)(</ints>)", RegexOptions.Singleline);
    private static readonly Regex IntRx = new(@"<int>(-?\d+)</int>");

    private static string KeepFilled(string mine, string theirs)
    {
        if (mine == null || !mine.Contains("UseItemInteractArea")) return theirs;
        var here = new Dictionary<string, List<int>>();
        foreach (Match m in AreaRx.Matches(mine))
            here[m.Groups[2].Value] = IntRx.Matches(m.Groups[3].Value).Select(x => int.Parse(x.Groups[1].Value)).ToList();
        var outp = new StringBuilder();
        int last = 0;
        foreach (Match m in AreaRx.Matches(theirs))
        {
            outp.Append(theirs, last, m.Index - last);
            last = m.Index + m.Length;
            var vals = IntRx.Matches(m.Groups[3].Value).Select(x => int.Parse(x.Groups[1].Value)).ToList();
            if (!here.TryGetValue(m.Groups[2].Value, out var ours) || vals.Count != ours.Count) { outp.Append(m.Value); continue; }
            outp.Append(m.Groups[1].Value);
            for (int i = 0; i < vals.Count; i++) outp.Append("<int>").Append(Math.Max(vals[i], ours[i])).Append("</int>");
            outp.Append(m.Groups[4].Value);
        }
        return outp.Append(theirs, last, theirs.Length - last).ToString();
    }

    private static bool Apply(POI_SaveLoad p, string xml)
    {
        // A carried animal is its own place (ODPOI_WildHare); its saved state would yank it out of someone's arms.
        // The carrier's drop changes the host's copy, which then sends a fresh state.
        if (CarrySync.Involves(p.transform)) return true;
        SaveableMap map;
        try { map = ClassicElementStateBridge.Deserialize(Il2CppType.Of<SaveableMap>(), xml).Cast<SaveableMap>(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Places: bad state for {p.name}: {e.Message}"); return true; }
        WorldObjects.ApplyingRemote = true;
        try { p.ISaveableMapElement_RestoreSavedState(map); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Places: can't load {p.name}: {e.InnerException?.Message ?? e.Message}"); }
        finally { WorldObjects.ApplyingRemote = false; }
        if (_known != null) _known[p.uniqueID] = Signature(Capture(p, out _));
        return true;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || !HouseBlocks.Ready || RekaCoop.Sync.Find.Scanned<POI_SaveLoad>(5f) == null) return;

        if (_known == null)
        {
            if (session.Role == Role.Host) { _known = new Dictionary<string, string>(); return; }  // filled as places are checked
            if (_readySent) return;
            _readySent = true;
            var rw = session.BeginMessage(MsgType.Poi);
            rw.Put((byte)Kind.Ready);
            session.Send(rw, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + 5f;
            _queue = All().Values.ToList();
            _cursor = 0;
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                var w = Waiting[i];
                var p = _queue.FirstOrDefault(q => q.uniqueID == w.Key);
                if (p != null && Apply(p, Gunzip(w.Gz))) Waiting.RemoveAt(i);
                else if (Time.unscaledTime - w.Since > 30f) { Plugin.Logger.LogWarning($"Places: no {w.Key} here; dropped its state"); Waiting.RemoveAt(i); }
            }
        }

        // One place per step, about ten a second; places away from everyone only every FarSeconds.
        if (Time.unscaledTime < _nextStep || _queue.Count == 0) return;
        _nextStep = Time.unscaledTime + 0.1f;
        POI_SaveLoad poi = null;
        for (int tries = 0; poi == null && tries < 32; tries++)
        {
            if (_cursor >= _queue.Count) _cursor = 0;
            var q = _queue[_cursor++];
            if (q == null || !q.isInited) continue;
            if (NextCheck.TryGetValue(q.uniqueID, out var at) && Time.unscaledTime < at) continue;
            if (Time.unscaledTime - CheckedAt.GetValueOrDefault(q.uniqueID, float.MinValue) < FarSeconds
                && !PlayerSync.NearAnyPlayer(q.transform.position, NearRange)) continue;
            poi = q;
        }
        if (poi == null || CarrySync.Involves(poi.transform)) return;
        var key = poi.uniqueID;
        CheckedAt[key] = Time.unscaledTime;
        var parts = CaptureParts(poi, out var ms);
        if (ms > 20) NextCheck[key] = Time.unscaledTime + 30f;  // huge places: rarely
        if (parts != null)
        {
            var partsSig = Signature(parts);
            bool firstParts = !KnownParts.TryGetValue(key, out var kp);
            if (kp == partsSig) return;
            KnownParts[key] = partsSig;
            // The parts are the baseline: the whole capture (~65 ms for the trial site) waits for a change.
            if (firstParts) { _known.TryAdd(key, null); return; }
        }
        var xml = Capture(poi, out var fullMs);
        if (ms + fullMs > 15) Plugin.Logger.LogInfo($"[perf] places: {poi.name} parts {ms:0.0} ms + whole {fullMs:0.0} ms ({parts?.Length} / {xml?.Length} chars)");
        if (xml == null) return;
        var sig = Signature(xml);
        if (_known.TryGetValue(key, out var k) && k == sig) return;  // k is null when only the parts were seen before
        bool isNew = !_known.ContainsKey(key);
        var before = k;
        _known[key] = sig;
        if (isNew) return;  // first look at this place
        if (session.Role == Role.Client && !Near(poi))
        {
            var rq = session.BeginMessage(MsgType.Poi);
            rq.Put((byte)Kind.Request);
            rq.Put(key);
            session.Send(rq, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Places: {poi.name} changed here by itself; asked the host for its copy");
            return;
        }
        var w2 = session.BeginMessage(MsgType.Poi);
        w2.Put((byte)Kind.State);
        WriteState(w2, key, xml);
        session.Send(w2, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Places: {poi.name} ({key}) changed at {(before == null ? "its parts" : FirstDifference(before, sig))}; sent {xml.Length} chars");
    }

    private static bool Near(POI_SaveLoad poi)
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        return me == null || Vector3.Distance(me.transform.position, poi.transform.position) < GuestReach;
    }

    private static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 120);
        string Cut(string s) => s.Substring(from, Math.Min(s.Length - from, 200));
        return $"'{Cut(a)}' -> '{Cut(b)}'";
    }

    // Dev: load every place's own state back into it; lists the ones that fail or come out different.
    public static string RoundTrip()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var p in All().Values)
        {
            var xml = Capture(p, out _);
            if (xml == null) { bad.Add($"{p.name}: no capture"); continue; }
            n++;
            try
            {
                var map = ClassicElementStateBridge.Deserialize(Il2CppType.Of<SaveableMap>(), xml).Cast<SaveableMap>();
                p.ISaveableMapElement_RestoreSavedState(map);
            }
            catch (Exception e) { bad.Add($"{p.name}: {e.InnerException?.Message ?? e.Message}"); continue; }
            if (Signature(Capture(p, out _)) != Signature(xml)) bad.Add($"{p.name}: differs after");
        }
        return $"{n} places, {bad.Count} bad: {string.Join("; ", bad)}";
    }

    private static void WriteState(NetDataWriter w, string key, string xml)
    {
        var gz = Gzip(xml);
        w.Put(key);
        w.Put(gz.Length);
        w.Put(gz);
    }

    private static (string key, byte[] gz) ReadState(NetDataReader r)
    {
        string key = r.GetString();
        var gz = new byte[r.GetInt()];
        r.GetBytes(gz, gz.Length);
        return (key, gz);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var all = All();
            var w = session.BeginMessage(MsgType.Poi);
            w.Put((byte)Kind.Snapshot);
            var states = new List<(string, string)>();
            foreach (var (key, p) in all)
            {
                var xml = Capture(p, out _);
                if (xml == null) continue;
                states.Add((key, xml));
                if (_known != null) _known[key] = Signature(xml);
            }
            w.Put(states.Count);
            foreach (var (key, xml) in states) WriteState(w, key, xml);
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo($"Places: sent {states.Count} places ({w.Length / 1024} KB) to player {from.PlayerId}");
        }
        else if (kind == Kind.Request && session.Role == Role.Host)
        {
            string key = r.GetString();
            if (!All().TryGetValue(key, out var p)) return;
            var xml = Capture(p, out _);
            if (xml == null) return;
            var w = session.BeginMessage(MsgType.Poi);
            w.Put((byte)Kind.State);
            WriteState(w, key, xml);
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
        }
        else if (kind == Kind.Snapshot)
        {
            int n = r.GetInt(), changed = 0;
            var all = All();
            _known = new Dictionary<string, string>();
            for (int i = 0; i < n; i++)
            {
                var (key, gz) = ReadState(r);
                var xml = Gunzip(gz);
                var sig = Signature(xml);
                if (all.TryGetValue(key, out var p))
                {
                    var mine = Capture(p, out _);
                    if (Signature(mine) == sig) { _known[key] = sig; continue; }
                    changed++;
                    Apply(p, xml);
                }
                else
                {
                    changed++;
                    Waiting.Add(new Pending { Key = key, Gz = gz, Since = Time.unscaledTime });
                }
            }
            Plugin.Logger.LogInfo($"Places: host's {n} places, {changed} differed here");
        }
        else if (kind == Kind.State)
        {
            var (key, gz) = ReadState(r);
            if (session.Role == Role.Host)
            {
                var w = session.BeginMessage(MsgType.Poi);
                w.Put((byte)Kind.State);
                w.Put(key);
                w.Put(gz.Length);
                w.Put(gz);
                session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
            }
            if (_known == null) return;  // the snapshot will carry it
            var all = All();
            if (all.TryGetValue(key, out var p)) Apply(p, KeepFilled(Capture(p, out _), Gunzip(gz)));
            else Waiting.Add(new Pending { Key = key, Gz = gz, Since = Time.unscaledTime });
            Plugin.Logger.LogInfo($"Places: player {from.PlayerId} changed {(p != null ? p.name : key)}");
        }
    }

    private static byte[] Gzip(string s)
    {
        var data = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
