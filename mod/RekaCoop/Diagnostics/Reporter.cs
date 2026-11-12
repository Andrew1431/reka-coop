using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Diagnostics;

// Playtest log reporter (pre-alpha: on unless turned off). Records this run's mod log, bundles it with the run's
// desync.log lines and versions when the game quits, crashes (sent on the next launch) or someone presses the report
// key, scrubs it and uploads it to server/log-reporter. Nothing is sent before the player has seen the notice.
// Files: BepInEx/RekaCoop-reports/{current,outbox,sent,rejected}. Runs whenever the mod is on.
internal static class Reporter
{
    // Set once server/log-reporter is deployed. Empty: bundles wait in outbox/.
    public const string DefaultEndpoint = "";

    private static ConfigEntry<bool> _enabled, _noticeShown, _unityErrors;
    private static ConfigEntry<string> _endpoint;
    private static ConfigEntry<KeyCode> _reportKey;

    private static bool _on;
    private static SessionLog _log;
    private static string _root, _testerId, _sessionId;
    private static bool _ended;
    private static int _patchFailures;

    public static bool On => _on;
    // Somewhere to send to. Without one (the report server isn't set up) reports are only saved on this PC, and
    // nothing on screen may say they were sent.
    private static bool Sends => _enabled.Value && _noticeShown.Value && !string.IsNullOrWhiteSpace(_endpoint.Value);
    // For the status line: the key that opens "report a problem".
    public static string KeyHint => _on && !_ended ? $"{_reportKey.Value}: report a problem" : null;
    private const string SavedWhere = "BepInEx\\RekaCoop-reports\\outbox in the game folder";
    public static string TesterId => _testerId;

    public static void Init(ConfigFile config)
    {
        try
        {
            if (Instance.Role == Role.None) return;
#if DEVBUILD
            const bool defaultOn = false;  // test copies: nothing leaves the PC unless turned on
#else
            const bool defaultOn = true;
#endif
            _enabled = config.Bind("Diagnostics", "UploadLogs", defaultOn,
                "Pre-alpha playtest: send the mod's log lines, desync.log and versions to the mod's developer when the game quits or crashes, " +
                "or when you press the report key. Names, Steam ids, your Windows user name, PC name and IP addresses are removed first. Saves are never sent.");
            _endpoint = config.Bind("Diagnostics", "Endpoint", DefaultEndpoint, "Where reports go.");
            _reportKey = config.Bind("Diagnostics", "ReportKey", KeyCode.F8, "Opens \"report a problem\" (a one-line note; everyone in the session sends their logs).");
            _noticeShown = config.Bind("Diagnostics", "NoticeShown", false, "The first-launch notice about log uploads was shown. Nothing is sent before it is.");
            _unityErrors = config.Bind("Diagnostics", "CaptureUnityErrors", false, "Also record the game's own exceptions (Unity log). Off: untested on every game build.");

            _root = Path.Combine(BepInEx.Paths.BepInExRootPath, "RekaCoop-reports");
            Uploader.Init(_root);
            Uploader.Allowed = () => _enabled.Value && _noticeShown.Value;
            Uploader.Endpoint = () => _endpoint.Value;
            _testerId = LoadTesterId();
            _sessionId = RandomHex(4);
            var current = Path.Combine(_root, "current");
            Directory.CreateDirectory(current);
            var previous = Directory.GetDirectories(current);

            _log = new SessionLog(Path.Combine(current, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{_sessionId}"));
            _log.SetState("id", _sessionId);
            _log.SetState("startedUtc", DateTime.UtcNow.ToString("o"));
            _log.SetState("mod", Plugin.PluginVersion);
            _log.SetState("protocol", Protocol.Version.ToString());
            _log.SetState("role", Instance.Role.ToString());
            _log.SetState("devBuild", IsDev ? "true" : "false");
            _log.SetState("game", SafeGameVersion());
            var desync = Path.Combine(BepInEx.Paths.GameRootPath, "desync.log");
            _log.SetState("desyncOffset", (File.Exists(desync) ? new FileInfo(desync).Length : 0).ToString());
            BepInEx.Logging.Logger.Listeners.Add(_log);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            _on = true;

            // Runs left behind by the last launch: no "endedUtc" means it crashed or was killed.
            foreach (var dir in previous) BundleOldRun(dir);
            Uploader.Kick();
        }
        catch (Exception e)
        {
            _on = false;
            Plugin.Logger.LogWarning($"Reports: off ({e.Message})");
        }
    }

    private static bool IsDev =>
#if DEVBUILD
        true;
#else
        false;
#endif

    private static void BundleOldRun(string dir)
    {
        try
        {
            var state = SessionLog.ReadState(dir);
            var kind = state.ContainsKey("endedUtc") ? "session" : "crash";
            Uploader.Queue(Bundle.Build(dir, _testerId, new Bundle.Extra { Kind = kind }), kind);
            Directory.Delete(dir, true);  // raw, unscrubbed copy; the bundle replaces it
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Reports: couldn't bundle {Path.GetFileName(dir)}: {e.Message}");
            try { Directory.Move(dir, Path.Combine(_root, "rejected", Path.GetFileName(dir))); } catch { }
        }
    }

    // Plugin.Load: Harmony's PatchAll stops at the first failing class (and takes the plugin down with it); this
    // patches class by class and logs a failure with its stack.
    public static void PatchAll(Harmony harmony)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e)
            {
                _patchFailures++;
                Plugin.Logger.LogError($"Harmony patch failed: {type.FullName}: {e}");
            }
        }
        if (_patchFailures > 0) _log?.SetState("patchFailures", _patchFailures.ToString());
    }

    // After Il2CppFixes (delegate types need injection). Main-thread messages only.
    public static void HookUnityLog()
    {
        if (!_on || !_unityErrors.Value) return;
        try
        {
            Application.add_logMessageReceived(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Application.LogCallback>(
                new Action<string, string, LogType>(OnUnityLog)));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Reports: no Unity log ({e.Message})"); }
    }

    private static int _unityLines;
    private static string _lastUnity;
    private static void OnUnityLog(string condition, string stack, LogType type)
    {
        if (type is not (LogType.Exception or LogType.Error or LogType.Assert) || _unityLines >= 300 || condition == _lastUnity) return;
        _unityLines++;
        _lastUnity = condition;
        _log?.Write($"[{DateTime.Now:HH:mm:ss.fff}] [Unity {type}] {condition}\n{stack}".TrimEnd());
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            _log?.Write($"[{DateTime.Now:HH:mm:ss.fff}] [diag] unhandled exception{(e.IsTerminating ? " (terminating)" : "")}: {e.ExceptionObject}");
            if (e.IsTerminating) Finish("crash", 3000);
        }
        catch { }
    }

    // ---- per frame (CoopDriver)

    private static float _nextNames, _nextMeta = 60f, _nextDesync = 300f;
    private static float _fpsTime;
    private static int _fpsFrames;

    public static void Tick(CoopSession session)
    {
        if (!_on || _ended) return;
        _fpsTime += Time.unscaledDeltaTime;
        _fpsFrames++;
        float now = Time.unscaledTime;
        if (now >= _nextNames)
        {
            _nextNames = now + 1f;
            LearnNames(session);
        }
        if (now >= _nextMeta)
        {
            _nextMeta = now + 60f;
            var meta = Meta(session);
            _log.Write($"[{DateTime.Now:HH:mm:ss.fff}] [diag] " + string.Join(" ", meta.Select(kv => $"{kv.Key}={kv.Value}")), false);
            _fpsTime = 0; _fpsFrames = 0;
        }
        if (now >= _nextDesync)
        {
            _nextDesync = now + 300f;
            if (_log.Desync.TakeWindow() is string d) _log.Write($"[{DateTime.Now:HH:mm:ss.fff}] [diag] desync last 5 min: {d}");
            _log.SetState("desyncTotal", _log.Desync.Total());
        }
    }

    private static void LearnNames(CoopSession session)
    {
        try
        {
            if (Steamworks.SteamClient.IsValid)
            {
                _log.LearnName(Steamworks.SteamClient.Name, session is { IsConnected: true } ? session.LocalPlayerId : null);
                if (!_log.State.ContainsKey("gameBuild")) _log.SetState("gameBuild", Steamworks.SteamApps.BuildId.ToString());
            }
        }
        catch { }
        if (session == null) return;
        if (session.IsConnected || session.Role == Role.Host) _log.LearnName(session.LocalName, session.LocalPlayerId);
        foreach (var p in session.Players.Values)
            if (p.Ready && p.PlayerId > 0) _log.LearnName(p.Name, p.PlayerId);
        foreach (var p in session.Players.Values)
            if (p.Ready && Sync.NameTags.NameOf(p.PlayerId) is string n && n != $"player {p.PlayerId}") _log.LearnName(n, p.PlayerId);
    }

    // Free of names: players are counted and timed, not named.
    private static Dictionary<string, string> Meta(CoopSession session)
    {
        var m = new Dictionary<string, string>
        {
            ["role"] = session?.Role.ToString() ?? Instance.Role.ToString(),
            ["transport"] = Instance.ConnectAddress != null ? "udp" : "steam",
            ["scene"] = SafeScene(),
            ["fps"] = _fpsFrames > 0 ? (_fpsFrames / Math.Max(0.001f, _fpsTime)).ToString("0") : "?",
        };
        if (session != null)
        {
            m["state"] = session.State.StartsWith("connecting to") ? "connecting" : session.State.Split('(')[0].Trim();
            m["localId"] = session.LocalPlayerId.ToString();
            var ready = session.Players.Values.Where(p => p.Ready).ToList();
            m["peers"] = ready.Count.ToString();
            m["rttMs"] = string.Join(",", ready.Select(p => $"{p.PlayerId}:{p.RttMs:0}"));
            m["inWorld"] = Sync.PlayerSync.InWorld ? "yes" : "no";
            m["puppets"] = Sync.PlayerSync.PuppetCount.ToString();
        }
        return m;
    }

    private static string SafeScene()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
        catch { return "?"; }
    }

    private static string SafeGameVersion()
    {
        try { return Application.version; }
        catch { return ""; }
    }

    // ---- quitting (CoopDriver: Application.quitting, OnDestroy)

    public static void EndSession() => Finish("session", 4000);

    private static void Finish(string kind, int waitMs)
    {
        if (!_on || _ended) return;
        _ended = true;
        try
        {
            _log.SetState("desyncTotal", _log.Desync.Total());
            if (kind == "session") _log.SetState("endedUtc", DateTime.UtcNow.ToString("o"));
            _log.Write($"[{DateTime.Now:HH:mm:ss.fff}] [diag] {(kind == "session" ? "session ended" : "crash")}");
            BepInEx.Logging.Logger.Listeners.Remove(_log);
            _log.Close();
            Uploader.Queue(Bundle.Build(_log.Dir, _testerId, new Bundle.Extra { Kind = kind }), kind);
            Directory.Delete(_log.Dir, true);
            Uploader.Kick(waitMs);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Reports: {e.Message}"); }
    }

    // ---- "report a problem"

    private static float _lastLocalReport = -999f;
    private static readonly HashSet<string> SeenReports = new();
    private static float _lastRemoteReport = -999f;

    // Local key press: everyone in the session sends theirs under the same id.
    public static void ReportProblem(string note)
    {
        if (!_on || _ended) return;
        if (Time.unscaledTime - _lastLocalReport < 30f) { CoopDriver.Notice("A report was just sent. Wait a few seconds before the next one."); return; }
        _lastLocalReport = Time.unscaledTime;
        var id = RandomHex(6);
        note = Clip(note, 300);
        var session = CoopDriver.Session;
        int me = session?.LocalPlayerId ?? 0;
        if (session is { IsConnected: true }) ReportSync.Send(session, id, me, note);
        Snapshot(id, me, note, session);
        CoopDriver.Notice(Sends
            ? $"Problem reported (id {id}). Logs from everyone in the session are on their way. Thank you!"
            : $"Problem saved (id {id}) in {SavedWhere}. Nothing was sent: post the newest file from there on the mod's Discord with a line about what happened.");
    }

    // A report from another player (ReportSync): add ours to it. Untrusted: ids are checked, notes clipped, rate capped.
    // True when taken (the host then relays it).
    public static bool OnRemoteReport(string id, int fromPlayer, string note)
    {
        if (!_on || _ended || id == null || id.Length != 12 || !id.All(Uri.IsHexDigit)) return false;
        id = id.ToLowerInvariant();
        if (SeenReports.Contains(id) || Time.unscaledTime - _lastRemoteReport < 20f) return false;
        _lastRemoteReport = Time.unscaledTime;
        Snapshot(id, fromPlayer, Clip(note, 300), CoopDriver.Session);
        CoopDriver.Notice($"player {fromPlayer} reported a problem: {Clip(note, 80)}" + (Sends ? " (your log is sent with it)" : $" (your log is saved with it in {SavedWhere})"));
        return true;
    }

    private static void Snapshot(string id, int by, string note, CoopSession session)
    {
        SeenReports.Add(id);
        _log.Write($"[{DateTime.Now:HH:mm:ss.fff}] [diag] ===== REPORT {id} by player {by}: {note}");
        var x = new Bundle.Extra { Kind = "report", ReportId = id, Note = note, ReportedBy = $"player {by}", ReportedUtc = DateTime.UtcNow, Meta = Meta(session) };
        var dir = _log.Dir;
        // Scrubbing a few MB of log takes a moment; not on the game's frame.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Uploader.Queue(Bundle.Build(dir, _testerId, x), "report"); Uploader.Kick(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Reports: report {id}: {e.Message}"); }
        });
    }

    // ---- GUI (CoopDriver.OnGUI)

    private static bool _typing;
    private static string _note = "";
    private static bool _focus;

    public static void OnGUI()
    {
        if (!_on) return;
        // Off, or nowhere to send to: nothing leaves this PC, so there is nothing to ask.
        if (_enabled.Value && !_noticeShown.Value && !string.IsNullOrWhiteSpace(_endpoint.Value)) { DrawNotice(); return; }
        var e = Event.current;
        if (!_typing)
        {
            if (e.type == EventType.KeyDown && e.keyCode == _reportKey.Value && !_ended) { _typing = true; _note = ""; _focus = true; e.Use(); }
            return;
        }
        if (e.type == EventType.KeyDown && e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { _typing = false; e.Use(); ReportProblem(_note); return; }
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { _typing = false; e.Use(); return; }

        var r = new Rect(Screen.width / 2f - 360, Screen.height / 2f - 50, 720, 100);
        GUI.Box(r, "");
        GUI.Box(r, "");
        GUI.Label(new Rect(r.x + 12, r.y + 8, r.width - 24, 22), Sends ? "Report a problem: what went wrong? (Enter sends everyone's logs, Esc cancels)" : "Report a problem: what went wrong? (Enter saves everyone's logs on their PCs, Esc cancels)");
        GUI.SetNextControlName("RekaReportNote");
        _note = GUI.TextField(new Rect(r.x + 12, r.y + 36, r.width - 24, 24), _note ?? "", 300);
        if (_focus) { GUI.FocusControl("RekaReportNote"); _focus = false; }
        if (GUI.Button(new Rect(r.x + r.width - 192, r.y + 68, 84, 24), "Send")) { _typing = false; ReportProblem(_note); }
        if (GUI.Button(new Rect(r.x + r.width - 96, r.y + 68, 84, 24), "Cancel")) _typing = false;
    }

    public const string NoticeText =
        "Reka Co-op is a pre-alpha test. To help find bugs, the mod sends a report to its developer when you quit, when the game crashes, " +
        "and when you press {0} to report a problem.\n\n" +
        "Sent: the mod's own lines from BepInEx\\LogOutput.log, this session's desync.log, the mod, game and protocol versions, " +
        "session details (role, player count, ping, scene, frame rate) and a random tester id.\n" +
        "Removed first: player and Steam names (they become \"player 1\"...), Steam ids, your Windows user name, PC name and IP addresses. " +
        "Saves are never sent.\n" +
        "Where: a Cloudflare server run by the mod's developer, kept only to fix the mod.\n\n" +
        "To turn it off later: set UploadLogs = false in BepInEx\\config\\" + Plugin.PluginGuid + ".cfg.";

    private static void DrawNotice()
    {
        var r = new Rect(Screen.width / 2f - 380, Screen.height / 2f - 170, 760, 340);
        GUI.Box(r, "");
        GUI.Box(r, "");
        GUI.Box(r, "");
        GUI.Label(new Rect(r.x + 16, r.y + 10, r.width - 32, 24), "Reka Co-op: log reports");
        GUI.Label(new Rect(r.x + 16, r.y + 38, r.width - 32, r.height - 90), string.Format(NoticeText, _reportKey.Value));
        if (GUI.Button(new Rect(r.x + r.width - 336, r.y + r.height - 40, 150, 28), "OK, send reports"))
        {
            _noticeShown.Value = true;
            Uploader.Kick();
        }
        if (GUI.Button(new Rect(r.x + r.width - 176, r.y + r.height - 40, 160, 28), "Don't send anything"))
        {
            _enabled.Value = false;
            _noticeShown.Value = true;
            Plugin.Logger.LogInfo("Reports: uploads turned off by the player");
        }
    }

    // ---- helpers

    private static string LoadTesterId()
    {
        var path = Path.Combine(BepInEx.Paths.ConfigPath, "RekaCoop", "tester-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var s = File.ReadAllText(path).Trim().ToLowerInvariant();
                if (s.Length == 32 && s.All(Uri.IsHexDigit)) return s;
            }
        }
        catch { }
        var id = RandomHex(16);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, id);
        return id;
    }

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string Clip(string s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] : s;
    }
}
