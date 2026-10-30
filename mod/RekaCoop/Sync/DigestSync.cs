using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// A desync detector for playtests. Every 15 s the host sends a hash of each shared state (story flags, trials, saved
// progress components, story objects on/off, the pack); a guest hashes its own. A category that differs twice in a row
// (so changes still in flight don't count) asks the host for the whole list, and the differences go to the log and to
// desync.log in the game folder, so a playtest's "she couldn't see X" can be read off afterwards. Story objects on/off
// and trial progress also repair themselves: the guest takes the host's values.
public static class DigestSync
{
    private enum Kind : byte { Digest = 1, DetailRequest = 2, Detail = 3 }

    private const float Interval = 15f;
    private const float DetailCooldown = 60f;

    private static readonly (string name, Func<IEnumerable<KeyValuePair<string, string>>> snapshot)[] Categories =
    {
        ("story", StorySync.Snapshot),
        ("trials", TrialSync.Snapshot),
        ("saveables", SaveableSync.Snapshot),
        ("active", ActiveSync.Snapshot),
        ("pack", InventorySync.Snapshot),
    };

    private static float _nextRound;
    private static int _next = -1;                                      // category being hashed this round
    private static IEnumerator<KeyValuePair<string, string>> _walk;    // its entries, read a couple of ms a frame
    private static ulong _sum;
    private static double _cost;
    private const double FrameBudgetMs = 1.5;
    private static readonly Dictionary<string, ulong?> Ours = new();
    private static readonly Dictionary<string, int> Misses = new();     // guest: rounds in a row a category differed
    private static readonly Dictionary<string, float> AskedAt = new();  // guest: last detail request per category
    private static readonly Dictionary<string, string> LastResult = new();
    private static readonly Dictionary<string, double> Cost = new();     // dev: ms to snapshot and hash

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Digest) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _next = -1;
        _walk = null;
        _nextRound = Time.unscaledTime + Interval;
        Ours.Clear();
        Misses.Clear();
        LastResult.Clear();
    }

    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037;
        foreach (var c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    // Order-free (a sum of each entry's hash), so the 14k story objects need no sorting and can be read over frames.
    private static ulong EntryHash(string key, string value)
    {
        ulong h = 14695981039346656037;
        foreach (var c in key) { h ^= c; h *= 1099511628211; }
        h ^= '='; h *= 1099511628211;
        foreach (var c in value) { h ^= c; h *= 1099511628211; }
        return h;
    }

    private static Dictionary<string, string> Snapshot(string name)
    {
        try
        {
            var all = Categories.First(c => c.name == name).snapshot();
            if (all == null) return null;
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in all) d[k] = v;
            return d;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Digest: {name}: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
    }

    // Called once the local body is in the world. Both sides hash on the same 15 s rhythm; the host sends its round.
    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || SaveLoadGame.isLoading) return;
        if (_next < 0)
        {
            if (Time.unscaledTime < _nextRound) return;
            _nextRound = Time.unscaledTime + Interval;
            _next = 0;
        }
        if (_next < Categories.Length)
        {
            var (name, snapshot) = Categories[_next];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_walk == null)
                {
                    var all = snapshot();
                    if (all == null) { Ours[name] = null; _next++; return; }
                    _walk = all.GetEnumerator();
                    _sum = 0;
                    _cost = 0;
                }
                bool more;
                while ((more = _walk.MoveNext()) && sw.Elapsed.TotalMilliseconds < FrameBudgetMs)
                    _sum += EntryHash(_walk.Current.Key, _walk.Current.Value);
                if (more) { _sum += EntryHash(_walk.Current.Key, _walk.Current.Value); _cost += sw.Elapsed.TotalMilliseconds; return; }
                Ours[name] = _sum;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Digest: {name}: {e.InnerException?.Message ?? e.Message}");
                Ours[name] = null;
            }
            Cost[name] = _cost + sw.Elapsed.TotalMilliseconds;
            _walk = null;
            _next++;
            return;
        }
        _next = -1;
        if (session.Role != Role.Host) return;
        var w = session.BeginMessage(MsgType.Digest);
        w.Put((byte)Kind.Digest);
        w.Put(Categories.Length);
        foreach (var (name, _) in Categories)
        {
            w.Put(name);
            var h = Ours.GetValueOrDefault(name);
            w.Put(h.HasValue);
            w.Put(h ?? 0);
        }
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Digest && session.Role == Role.Client)
        {
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                var name = r.GetString();
                bool has = r.GetBool();
                ulong theirs = r.GetULong();
                // Ours are from our last round, at most 15 s old; a real desync lasts, so it shows twice running.
                if (!has || Ours.GetValueOrDefault(name) is not ulong mine) continue;
                if (mine == theirs)
                {
                    if (Misses.GetValueOrDefault(name) >= 2) Log($"{name}: matches the host again");
                    Misses[name] = 0;
                    LastResult[name] = "same";
                    continue;
                }
                Misses[name] = Misses.GetValueOrDefault(name) + 1;
                LastResult[name] = $"differs ({Misses[name]} rounds)";
                if (Misses[name] < 2 || Time.unscaledTime < AskedAt.GetValueOrDefault(name, -DetailCooldown) + DetailCooldown) continue;
                AskedAt[name] = Time.unscaledTime;
                var w = session.BeginMessage(MsgType.Digest);
                w.Put((byte)Kind.DetailRequest);
                w.Put(name);
                session.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }
        else if (kind == Kind.DetailRequest && session.Role == Role.Host)
        {
            var name = r.GetString();
            var state = Snapshot(name);
            if (state == null) return;
            var sb = new StringBuilder();
            foreach (var (k, v) in state) sb.Append(k).Append('\t').Append(v).Append('\n');
            var w = session.BeginMessage(MsgType.Digest);
            w.Put((byte)Kind.Detail);
            w.Put(name);
            var gz = Pack(sb.ToString());
            w.Put(gz.Length);  // PutBytesWithLength caps at 64 KB; the story objects' list is bigger
            w.Put(gz);
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
        }
        else if (kind == Kind.Detail && session.Role == Role.Client)
        {
            var name = r.GetString();
            var host = new Dictionary<string, string>();
            var gz = new byte[r.GetInt()];
            r.GetBytes(gz, gz.Length);
            foreach (var line in Unpack(gz).Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0) host[line[..tab]] = line[(tab + 1)..];
            }
            var mine = Snapshot(name) ?? new();
            var diffs = host.Keys.Union(mine.Keys)
                .Where(k => host.GetValueOrDefault(k) != mine.GetValueOrDefault(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => $"{k}: host {host.GetValueOrDefault(k) ?? "-"}, here {mine.GetValueOrDefault(k) ?? "-"}")
                .ToList();
            LastResult[name] = $"{diffs.Count} differ";
            Log($"{name}: {diffs.Count} differ from the host" + (diffs.Count > 0 ? ":\n  " + string.Join("\n  ", diffs) : ""));
            if (name == "active")
            {
                var fix = new List<(string, bool)>();
                foreach (var (k, v) in host)
                    if (mine.TryGetValue(k, out var here) && here != v) fix.Add((k, v == "1"));
                if (fix.Count > 0) Log($"active: switched {ActiveSync.MatchHost(fix)} of {fix.Count} to match the host");
            }
            else if (name == "trials" && diffs.Count > 0) Log($"trials: {TrialSync.MatchHost(host)} values changed to match the host");
        }
    }

    private static void Log(string text)
    {
        var shortText = text.Length > 1500 ? text[..1500] + " ..." : text;
        Plugin.Logger.LogWarning($"Desync {shortText}");
        try { File.AppendAllText(Path.Combine(BepInEx.Paths.GameRootPath, "desync.log"), $"[{DateTime.Now:HH:mm:ss}] {text}\n"); }
        catch (Exception) { }
    }

    private static byte[] Pack(string s)
    {
        var data = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string Unpack(byte[] data)
    {
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public static string Describe() =>
        string.Join("; ", Categories.Select(c => $"{c.name} {LastResult.GetValueOrDefault(c.name) ?? (Ours.GetValueOrDefault(c.name).HasValue ? "hashed" : "not ready")} ({Cost.GetValueOrDefault(c.name):0.0} ms)"));
}
