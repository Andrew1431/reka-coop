using System;
using System.IO;
using System.Linq;

namespace RekaCoop.Appearance;

// The player's co-op look, kept in the mod's own config folder so it never depends on any save.
// Created on first use by importing Character.xml from the player's most recent save, if there is one.
internal static class AppearanceProfile
{
    public static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "RekaCoop", "appearance.xml");

    public static AppearanceData Load()
    {
        try
        {
            if (File.Exists(FilePath)) return AppearanceData.FromXml(File.ReadAllText(FilePath));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Appearance profile unreadable, ignoring it: {e.Message}");
        }
        return null;
    }

    public static void Save(AppearanceData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, data.ToXml());
    }

    // Newest Character.xml under the game's save root (the real one, or a test copy's override).
    public static AppearanceData ImportFromSaves(string saveRoot)
    {
        if (string.IsNullOrEmpty(saveRoot) || !Directory.Exists(saveRoot)) return null;
        var newest = new DirectoryInfo(saveRoot)
            .EnumerateFiles("Character.xml", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest == null) return null;
        Plugin.Logger.LogInfo($"Imported appearance from {newest.FullName}");
        return AppearanceData.FromXml(File.ReadAllText(newest.FullName));
    }
}
