using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace RekaCoop.Diagnostics;

// One upload: gzip of a JSON object. The Worker (server/log-reporter) checks schema, mod, protocol, kind, testerId and
// reportId; everything else is read by server/log-reporter/pull.py. Everything that can hold private text is scrubbed.
// Never reads saves.
internal static class Bundle
{
    public const int MaxGzipBytes = 900 * 1024;  // the Worker takes 1 MB
    private const long MaxLog = SessionLog.MaxBytes;
    private const long MaxDesync = 1 << 20;

    public sealed class Extra
    {
        public string Kind = "session";     // session / crash / report
        public string ReportId;
        public string Note;
        public string ReportedBy;           // label of who pressed the report key
        public DateTime? ReportedUtc;
        public Dictionary<string, string> Meta;  // live session metadata (players, scene, RTT), already free of names
    }

    public static byte[] Build(string sessionDir, string testerId, Extra x)
    {
        var state = SessionLog.ReadState(sessionDir);
        var scrub = new Scrubber(testerId, SessionLog.ReadNames(sessionDir));
        var log = SessionLog.ReadTail(Path.Combine(sessionDir, "log.txt"), 0, MaxLog, out bool logCut);
        long.TryParse(state.GetValueOrDefault("desyncOffset"), out long desyncFrom);
        var desync = SessionLog.ReadTail(Path.Combine(BepInEx.Paths.GameRootPath, "desync.log"), desyncFrom, MaxDesync, out bool desyncCut);
        log = scrub.Clean(log);
        desync = scrub.Clean(desync);

        // Too big: drop the oldest log lines first; the end of a session is what usually matters.
        for (int keep = log.Length; ; keep /= 2)
        {
            var bytes = Gzip(Json(state, testerId, x, scrub, keep < log.Length ? CutHead(log, keep) : log, logCut || keep < log.Length, desync, desyncCut));
            if (bytes.Length <= MaxGzipBytes || keep < 4096) return bytes;
        }
    }

    private static string CutHead(string s, int keep)
    {
        var t = s[^keep..];
        int nl = t.IndexOf('\n');
        return nl >= 0 ? t[(nl + 1)..] : t;
    }

    private static byte[] Json(Dictionary<string, string> state, string testerId, Extra x, Scrubber scrub, string log, bool logCut, string desync, bool desyncCut)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("mod", state.GetValueOrDefault("mod") ?? Plugin.PluginVersion);
            w.WriteNumber("protocol", int.TryParse(state.GetValueOrDefault("protocol"), out int p) ? p : Net.Protocol.Version);
            w.WriteString("game", state.GetValueOrDefault("game") ?? "");
            w.WriteString("gameBuild", state.GetValueOrDefault("gameBuild") ?? "");
            w.WriteString("kind", x.Kind);
            w.WriteString("testerId", testerId);
            if (x.ReportId != null) w.WriteString("reportId", x.ReportId); else w.WriteNull("reportId");
            if (x.Note != null) w.WriteString("note", scrub.Clean(x.Note));
            if (x.ReportedBy != null) w.WriteString("reportedBy", scrub.Clean(x.ReportedBy));
            if (x.ReportedUtc is DateTime t) w.WriteString("reportedUtc", t.ToString("o"));
            w.WriteStartObject("session");
            foreach (var key in new[] { "id", "startedUtc", "endedUtc", "role", "devBuild", "desyncTotal" })
                if (state.TryGetValue(key, out var v)) w.WriteString(key, scrub.Clean(v));
            w.WriteBoolean("cleanExit", state.ContainsKey("endedUtc"));
            w.WriteEndObject();
            if (x.Meta != null)
            {
                w.WriteStartObject("meta");
                foreach (var (k, v) in x.Meta) w.WriteString(k, scrub.Clean(v));
                w.WriteEndObject();
            }
            w.WriteString("log", log);
            w.WriteBoolean("logCut", logCut);
            w.WriteString("desync", desync);
            w.WriteBoolean("desyncCut", desyncCut);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
