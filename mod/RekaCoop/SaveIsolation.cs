using System.IO;

namespace RekaCoop;

// Points the game's save root at a per-instance folder using the game's own FileIO.SaveRootOverride.
// Apply must run before FileIO.Init, i.e. from Plugin.Load. A player who accepts an invite while the game runs (Auto)
// switches to the guest folder then, and back to their own saves when the session ends.
internal static class SaveIsolation
{
    private static string _ownRoot;

    public static void UseGuestRoot()
    {
        Directory.CreateDirectory(Instance.SaveRoot);
        FileIO.SaveRootOverride = Instance.SaveRoot;
        if (FileIO.InstanceIfExists is not { isInitialised: true } fileIO) return;  // Init picks up the override
        if (string.Equals(FileIO.SaveFilesRootPath, Instance.SaveRoot, System.StringComparison.OrdinalIgnoreCase)) return;
        _ownRoot = FileIO.SaveFilesRootPath;
        FileIO.SaveFilesRootPath = Instance.SaveRoot;
        fileIO.EnsureSaveRootExists();
        fileIO.Rescan();
        Plugin.Logger.LogInfo($"Saves: guest folder {Instance.SaveRoot} (own saves at {_ownRoot} untouched)");
    }

    public static void UseOwnRoot()
    {
        if (_ownRoot == null) return;
        FileIO.SaveRootOverride = null;
        FileIO.SaveFilesRootPath = _ownRoot;
        FileIO.InstanceIfExists?.Rescan();
        Plugin.Logger.LogInfo($"Saves: back to own saves at {_ownRoot}");
        _ownRoot = null;
    }

    public static void Apply()
    {
        if (!Instance.IsIsolated) return;

        Directory.CreateDirectory(Instance.SaveRoot);
        FileIO.SaveRootOverride = Instance.SaveRoot;
        Plugin.Logger.LogInfo($"Save root override -> {Instance.SaveRoot}");
    }

    // Logged once FileIO is up so we can see what the game actually resolved.
    public static string Describe()
    {
        var fileIO = FileIO.InstanceIfExists;
        if (fileIO == null) return "FileIO not created";
        return $"SaveFilesRootPath={FileIO.SaveFilesRootPath} BlueprintLibraryPath={FileIO.BlueprintLibraryPath} initialised={fileIO.isInitialised}";
    }
}
