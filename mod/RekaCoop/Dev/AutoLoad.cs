#if DEVBUILD
using System;
using UnityEngine;

namespace RekaCoop.Dev;

// `-coopautoload` (host): once the main menu is up and saves are scanned, load the most recent save.
// `-coopautoloadslot N`: load slot N of folder 0 instead, and only once a guest has connected (tests a guest who
// joined while the host was still at the menu).
internal static class AutoLoad
{
    private static bool _enabled = Array.Exists(Environment.GetCommandLineArgs(),
        a => string.Equals(a, "-coopautoload", StringComparison.OrdinalIgnoreCase));
    private static float _readySince = -1;
    private static bool _done;
    private static readonly int _slot = SlotArg();

    private static int SlotArg()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(args, a => string.Equals(a, "-coopautoloadslot", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? n : -1;
    }

    public static void Tick()
    {
        if (_done || (!_enabled && _slot < 0) || Instance.Role == Net.Role.Client) return;  // a guest loads the world the host sends
        if (_slot >= 0 && CoopDriver.Session is not { IsConnected: true }) return;
        var fileIO = FileIO.InstanceIfExists;
        var menu = UnityEngine.Object.FindFirstObjectByType<MainMenu>();
        bool ready = fileIO != null && fileIO.status == FileIO.Status.Loaded && menu != null && !menu.isLoading;
        if (!ready) { _readySince = -1; return; }

        // Let the menu finish its fade-in before loading.
        if (_readySince < 0) _readySince = Time.unscaledTime;
        if (Time.unscaledTime - _readySince < 3f) return;

        _done = true;
        if (_slot >= 0)
        {
            Plugin.Logger.LogInfo($"AutoLoad: loading folder 0 slot {_slot}");
            menu.LoadSaveGame(0, _slot);
            return;
        }
        if (!fileIO.TryGetMostRecentSave(out SaveGame save, out int folder, out int slot))
        {
            Plugin.Logger.LogWarning("AutoLoad: no save found");
            return;
        }
        Plugin.Logger.LogInfo($"AutoLoad: loading '{save.userDefinedName}' folder {folder} slot {slot}");
        menu.LoadSaveGame(folder, slot);
    }
}
#endif
