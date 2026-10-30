using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace RekaCoop.Diagnostics;

// This run's log, kept raw on the player's own disk until it is bundled (scrubbed then). LogOutput.log can't be used
// for a crashed run: BepInEx starts it afresh before plugins load. Holds the mod's lines and every other source's
// warnings and errors (Il2CppInterop trampoline exceptions, Harmony), plus the names seen, for scrubbing later.
//   <dir>/log.txt     lines as logged
//   <dir>/names.tsv   name \t label ("player N")
//   <dir>/state.txt   key=value: started, role, game, desync offset, ended
internal sealed class SessionLog : ILogListener
{
    public const long MaxBytes = 6 << 20;
    private const long InfoBytes = 4 << 20;  // past this only warnings and errors are kept

    public readonly string Dir;
    private readonly object _lock = new();
    private readonly StreamWriter _log;
    private long _written;
    private bool _capped;
    public readonly Dictionary<string, string> State = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _letters;

    public SessionLog(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _log = new StreamWriter(new FileStream(Path.Combine(dir, "log.txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public LogLevel LogLevelFilter => LogLevel.All;

    public void LogEvent(object sender, LogEventArgs e)
    {
        try
        {
            bool ours = e.Source?.SourceName == Plugin.PluginName;
            bool bad = (e.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0;
            if (!ours && !bad) return;
            var msg = e.Data?.ToString() ?? "";
            if (ours) Watch(msg);
            Write($"[{DateTime.Now:HH:mm:ss.fff}] [{e.Level,-7}:{e.Source?.SourceName}] {msg}", bad);
        }
        catch { }  // never let logging break the game
    }

    public void Write(string line, bool important = true)
    {
        lock (_lock)
        {
            if (_written > MaxBytes || (!important && _written > InfoBytes))
            {
                if (!_capped) { _capped = true; _log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [diag] log cap reached; dropping info lines"); }
                return;
            }
            _log.WriteLine(line);
            _written += line.Length + 2;
        }
    }

    // ---- names, learned from what the mod logs and from the session (CoopDriver.Update via Reporter.Tick)

    private static readonly Regex[] NamePatterns =
    {
        new(@"^Steam: up as (?<n>.+?); ", RegexOptions.Compiled),
        new(@"^Steam: (?<n>.+) (joined|left) the lobby$", RegexOptions.Compiled),
        new(@"^\[net\] player (?<id>\d+) '(?<n>.+)' joined$", RegexOptions.Compiled),
        new(@"^Names: player (?<id>\d+) is (?<n>.+)$", RegexOptions.Compiled),
    };

    private void Watch(string msg)
    {
        foreach (var p in NamePatterns)
        {
            var m = p.Match(msg);
            if (m.Success) LearnName(m.Groups["n"].Value, m.Groups["id"].Success ? int.Parse(m.Groups["id"].Value) : null);
        }
        Desync.Watch(msg);
    }

    public readonly DesyncTally Desync = new();

    public void LearnName(string name, int? playerId)
    {
        if (!Scrubber.IsName(name)) return;
        name = name.Trim();
        lock (_lock)
        {
            if (_names.TryGetValue(name, out var had) && (playerId == null || had == $"player {playerId}")) return;
            if (playerId == null && had != null) return;
            // Unknown id (a Steam lobby member): a letter, so it can't be mistaken for a player id.
            _names[name] = playerId != null ? $"player {playerId}" : $"player {(char)('A' + Math.Min(25, _letters++))}";
            try { File.WriteAllLines(Path.Combine(Dir, "names.tsv"), _names.Select(kv => kv.Key.Replace('\t', ' ') + "\t" + kv.Value)); }
            catch { }
        }
    }

    public void SetState(string key, string value)
    {
        lock (_lock)
        {
            if (State.TryGetValue(key, out var v) && v == value) return;
            State[key] = value;
            try { File.WriteAllLines(Path.Combine(Dir, "state.txt"), State.Select(kv => $"{kv.Key}={kv.Value?.Replace('\n', ' ')}")); }
            catch { }
        }
    }

    public void Close()
    {
        lock (_lock) { try { _log.Dispose(); } catch { } }
    }

    public void Dispose() => Close();

    // ---- reading a session folder back (this run's, or a crashed run's on the next launch)

    public static Dictionary<string, string> ReadState(string dir)
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "state.txt")))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line[..eq]] = line[(eq + 1)..];
            }
        }
        catch { }
        return d;
    }

    public static List<(string name, string label)> ReadNames(string dir)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "names.tsv")))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0) list.Add((line[..tab], line[(tab + 1)..]));
            }
        }
        catch { }
        return list;
    }

    // The last `max` bytes of a file another writer may still hold open.
    public static string ReadTail(string path, long from, long max, out bool cut)
    {
        cut = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, Math.Min(from, fs.Length));
            if (fs.Length - start > max) { start = fs.Length - max; cut = true; }
            fs.Position = start;
            using var r = new StreamReader(fs, Encoding.UTF8);
            var s = r.ReadToEnd();
            if (cut) { int nl = s.IndexOf('\n'); if (nl >= 0) s = s[(nl + 1)..]; }
            return s;
        }
        catch { return ""; }
    }
}

// Counts the desync detector's findings (DigestSync's "Desync ..." warnings) for a periodic summary.
internal sealed class DesyncTally
{
    private static readonly Regex Differ = new(@"^Desync (?<c>\w+): (?<n>\d+) differ from the host", RegexOptions.Compiled);
    private static readonly Regex Switched = new(@"^Desync (?<c>\w+): switched (?<n>\d+) of", RegexOptions.Compiled);
    private static readonly Regex Changed = new(@"^Desync (?<c>\w+): (?<n>\d+) values changed to match", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly Dictionary<string, (int rounds, int keys, int repaired)> _window = new(), _total = new();

    public void Watch(string msg)
    {
        if (!msg.StartsWith("Desync ", StringComparison.Ordinal)) return;
        Match m;
        lock (_lock)
        {
            if ((m = Differ.Match(msg)).Success && m.Groups["n"].Value != "0") Add(m.Groups["c"].Value, 1, int.Parse(m.Groups["n"].Value), 0);
            else if ((m = Switched.Match(msg)).Success || (m = Changed.Match(msg)).Success) Add(m.Groups["c"].Value, 0, 0, int.Parse(m.Groups["n"].Value));
        }
    }

    private void Add(string c, int rounds, int keys, int repaired)
    {
        foreach (var d in new[] { _window, _total })
        {
            var v = d.TryGetValue(c, out var x) ? x : default;
            d[c] = (v.rounds + rounds, v.keys + keys, v.repaired + repaired);
        }
    }

    // "active 2 rounds, 5 keys, 5 repaired; trials ..." since the last call, or null if nothing happened.
    public string TakeWindow()
    {
        lock (_lock)
        {
            if (_window.Count == 0) return null;
            var s = Describe(_window);
            _window.Clear();
            return s;
        }
    }

    public string Total()
    {
        lock (_lock) return _total.Count == 0 ? "" : Describe(_total);
    }

    private static string Describe(Dictionary<string, (int rounds, int keys, int repaired)> d) =>
        string.Join("; ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.rounds} rounds, {kv.Value.keys} keys, {kv.Value.repaired} repaired"));
}
