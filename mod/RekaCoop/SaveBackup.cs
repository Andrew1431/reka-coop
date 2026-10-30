using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RekaCoop;

// A zip of the player's own saves on every start, before the game opens them, so nothing co-op does can cost a
// save. Kept next to the saves (not in the game folder, so uninstalling or verifying the game leaves them):
// the first backup ever made (the saves as they were before co-op) and the newest Keep.
internal static class SaveBackup
{
    private const int Keep = 10;
    internal static string SaveRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RekaGame", "Saves");
    internal static string BackupRoot => Path.Combine(Path.GetDirectoryName(SaveRoot)!, "RekaCoop-Backups");

    // Plugin.Load, before FileIO.Init.
    public static void Run()
    {
        if (Instance.IsIsolated) return;  // a dev copy with its own save folder: the real saves aren't in play
        try
        {
            if (!Directory.Exists(SaveRoot) || !Directory.EnumerateFiles(SaveRoot, "*", SearchOption.AllDirectories).Any()) return;
            Directory.CreateDirectory(BackupRoot);
            var first = !Directory.EnumerateFiles(BackupRoot, "*.zip").Any();
            var name = (first ? "before-coop_" : "") + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip";
            var zip = Path.Combine(BackupRoot, name);
            var started = DateTime.UtcNow;
            ZipFile.CreateFromDirectory(SaveRoot, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            Plugin.Logger.LogInfo($"Saves backed up to {zip} ({new FileInfo(zip).Length / 1024} KB, {(DateTime.UtcNow - started).TotalMilliseconds:0} ms)");

            foreach (var old in new DirectoryInfo(BackupRoot).GetFiles("*.zip")
                         .Where(f => !f.Name.StartsWith("before-coop_"))
                         .OrderByDescending(f => f.Name).Skip(Keep))
                old.Delete();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Saves: backup failed ({e.Message}); your saves themselves are untouched");
        }
    }
}
