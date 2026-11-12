using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace RekaCoop;

// Zips of the player's own saves, so nothing co-op does can cost a save. Kept next to the saves (not in the game
// folder, so uninstalling or verifying the game leaves them):
//   before-coop_<date>.zip  the saves as they were the first time the mod ran; kept forever
//   <date>.zip              on every start, before the game opens any save; the newest KeepStarts are kept
//   playing_<date>.zip      every Every minutes while playing in a world of one's own (the host's game is the one
//                           that saves the shared world, into the host's own save); the newest KeepPlaying are kept
internal static class SaveBackup
{
    private const int KeepStarts = 10, KeepPlaying = 12;
    private const float Every = 20f * 60f, RetryIn = 60f;
    // A dev test copy backs up its own save folder the same way, so the tests go through this code too.
    internal static string SaveRoot => Instance.Name != null ? Instance.SaveRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RekaGame", "Saves");
    internal static string BackupRoot => Path.Combine(Path.GetDirectoryName(SaveRoot)!, "RekaCoop-Backups");

    // Plugin.Load, before FileIO.Init.
    public static void Run()
    {
        if (Instance.Role == Net.Role.Client) return;  // started as a guest (dev): no saves of its own in play
        try
        {
            if (!HasSaves()) return;
            Directory.CreateDirectory(BackupRoot);
            var first = !Directory.EnumerateFiles(BackupRoot, "*.zip").Any();
            Make(first ? "before-coop_" : "");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Saves: backup failed ({e.Message}); your saves themselves are untouched");
        }
    }

    private static float _next = Every;
    private static Task _running;
    private static volatile bool _failed;

    // Dev: the next Tick in a world makes one now.
    internal static void Soon() => _next = 0;

    // Every frame (CoopDriver). Only while this game plays its own saves in a world, and never while the game writes.
    public static void Tick(float now, bool inOwnWorld)
    {
        if (_failed) { _failed = false; _next = now + RetryIn; }
        if (now < _next || _running is { IsCompleted: false }) return;
        if (!inOwnWorld) { _next = now + RetryIn; return; }
        if (FileIO.isSaving || SaveLoadGame.isLoading || SaveLoadGame.worldIsLoading) { _next = now + 5f; return; }
        _next = now + Every;
        // Off the game's frame: a few MB of saves take a moment to zip.
        _running = Task.Run(() =>
        {
            try { if (HasSaves()) Make("playing_"); }
            catch (Exception e)
            {
                // The game began a save meanwhile (a file was locked): the next try is soon.
                _failed = true;
                Plugin.Logger.LogWarning($"Saves: backup while playing failed ({e.Message}); trying again in a minute");
            }
        });
    }

    private static bool HasSaves() =>
        Directory.Exists(SaveRoot) && Directory.EnumerateFiles(SaveRoot, "*", SearchOption.AllDirectories).Any();

    private static void Make(string prefix)
    {
        Directory.CreateDirectory(BackupRoot);
        var zip = Path.Combine(BackupRoot, prefix + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip");
        var part = zip + ".part";
        var started = DateTime.UtcNow;
        try
        {
            if (File.Exists(part)) File.Delete(part);
            ZipFile.CreateFromDirectory(SaveRoot, part, CompressionLevel.Fastest, includeBaseDirectory: false);
            File.Move(part, zip);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
        Plugin.Logger.LogInfo($"Saves backed up to {zip} ({new FileInfo(zip).Length / 1024} KB, {(DateTime.UtcNow - started).TotalMilliseconds:0} ms)");
        Trim();
    }

    private static void Trim()
    {
        var all = new DirectoryInfo(BackupRoot).GetFiles("*.zip").Where(f => !f.Name.StartsWith("before-coop_")).ToList();
        var playing = all.Where(f => f.Name.StartsWith("playing_")).OrderByDescending(f => f.Name).Skip(KeepPlaying);
        var starts = all.Where(f => !f.Name.StartsWith("playing_")).OrderByDescending(f => f.Name).Skip(KeepStarts);
        foreach (var old in playing.Concat(starts))
            try { old.Delete(); } catch { }
    }
}
