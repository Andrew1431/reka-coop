using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace RekaCoop.Diagnostics;

// Sends finished bundles from outbox/ on a background thread. What can't be sent now (offline, server down, rate
// limited) stays in outbox/ for the next launch; what the server turns down for good moves to rejected/.
internal static class Uploader
{
    // Same value as UPLOAD_KEY in server/log-reporter/wrangler.toml. Not a secret.
    public const string UploadKey = "reka-coop-playtest-1";
    private const int KeepSent = 20;
    private const int MaxOutbox = 40;

    private static readonly object Lock = new();
    private static Thread _thread;
    private static volatile bool _again;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string Root, Outbox, Sent, Rejected;
    public static Func<bool> Allowed = () => false;
    public static Func<string> Endpoint = () => "";

    public static void Init(string root)
    {
        Root = root;
        Outbox = Path.Combine(root, "outbox");
        Sent = Path.Combine(root, "sent");
        Rejected = Path.Combine(root, "rejected");
        foreach (var d in new[] { Outbox, Sent, Rejected }) Directory.CreateDirectory(d);
    }

    public static string Queue(byte[] gz, string kind)
    {
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{kind}.json.gz";
        var path = Path.Combine(Outbox, name);
        File.WriteAllBytes(path + ".part", gz);
        File.Move(path + ".part", path);
        Trim(Outbox, MaxOutbox);
        return name;
    }

    // Starts sending if allowed. `waitMs` > 0 blocks up to that long (quitting: Steam and the process are going away).
    public static void Kick(int waitMs = 0)
    {
        if (!Allowed() || string.IsNullOrWhiteSpace(Endpoint())) return;
        Thread t;
        lock (Lock)
        {
            _again = true;
            if (_thread is not { IsAlive: true })
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "RekaCoop log upload" };
                _thread.Start();
            }
            t = _thread;
        }
        if (waitMs > 0) t.Join(waitMs);
    }

    // The server takes 6 uploads a minute from one address. A player can have dozens waiting (every session and
    // crash since the last time uploads worked), so: what a player reported by hand goes first, then the newest;
    // one every PaceMs; and when the server says "slow down" the rest waits a minute here instead of until the
    // next launch (seen: a fresh F8 report stuck behind twenty old ones).
    private const int PaceMs = 12000, LimitedWaitMs = 65000, MaxLimitedWaits = 6;

    private static void Run()
    {
        int limited = 0;
        bool first = true;
        while (_again)
        {
            _again = false;
            for (;;)
            {
                if (!Allowed()) return;
                string next;
                try
                {
                    next = Directory.GetFiles(Outbox, "*.json.gz")
                        .OrderByDescending(f => f.EndsWith("-report.json.gz", StringComparison.Ordinal))
                        .ThenByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
                }
                catch { return; }
                if (next == null) break;
                if (!first) Thread.Sleep(PaceMs);
                first = false;
                var result = Send(next);
                if (result == Result.Limited)
                {
                    if (++limited > MaxLimitedWaits) return;
                    Thread.Sleep(LimitedWaitMs);
                }
                else if (result == Result.Retry) return;  // offline or the server is down: the rest waits too
            }
        }
    }

    private enum Result { Sent, Rejected, Retry, Limited }

    private static Result Send(string file)
    {
        try
        {
            var body = File.ReadAllBytes(file);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint().TrimEnd('/') + "/v1/report") { Content = content };
            req.Headers.Add("X-Reka-Key", UploadKey);
            req.Headers.UserAgent.ParseAdd($"RekaCoop/{Plugin.PluginVersion}");
            using var resp = Http.Send(req);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) { Move(file, Sent); Trim(Sent, KeepSent); Plugin.Logger.LogInfo($"Reports: sent {Path.GetFileName(file)}"); return Result.Sent; }
            if (code == 429) { Plugin.Logger.LogInfo("Reports: the server says slow down; the rest goes in a minute"); return Result.Limited; }
            if (code == 408 || code >= 500) { Plugin.Logger.LogInfo($"Reports: server said {code}; will retry next launch"); return Result.Retry; }
            Move(file, Rejected);
            Trim(Rejected, KeepSent);
            Plugin.Logger.LogWarning($"Reports: {Path.GetFileName(file)} turned down ({code}" + (code == 426 ? ": this mod version is too old" : "") + ")");
            return Result.Rejected;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or WebException)
        {
            Plugin.Logger.LogInfo($"Reports: upload failed ({e.GetType().Name}); kept for next launch");
            return Result.Retry;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"Reports: upload failed ({e.GetType().Name}: {e.Message}); kept for next launch");
            return Result.Retry;
        }
    }

    private static void Move(string file, string dir)
    {
        var dest = Path.Combine(dir, Path.GetFileName(file));
        if (File.Exists(dest)) dest = Path.Combine(dir, Guid.NewGuid().ToString("N")[..6] + "-" + Path.GetFileName(file));
        File.Move(file, dest);
    }

    // Our own cache folder: only the newest `keep` files stay.
    private static void Trim(string dir, int keep)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.json.gz").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
                File.Delete(f);
        }
        catch { }
    }
}
