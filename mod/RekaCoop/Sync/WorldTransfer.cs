using System.Collections.Generic;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Joining: the host sends its current save to each guest that joins. The guest replaces its sandbox saves with it
// and loads it from the main menu, so both play the same world. Guests never save.
// Travelling: a new map is generated on the host alone (each machine would roll its own). The game saves when the
// map changes; that save goes to every guest, who loads it where they stand.
public static class WorldTransfer
{
    private static readonly System.Collections.Generic.List<int> PendingJoiners = new();  // host: still to be sent the world
    private static byte[] _received;     // guest: the host's world, waiting to be written and loaded
    private static bool _loadWhenReady;
    private static float _readySince = -1;
    private static bool _resendAfterSave;     // host: travelled, waiting for the save the game makes on arrival
    private static DateTime _travelledAt;
    private static float _nextSaveCheck;
    private static bool _inGameLoad;          // guest: the world arrived while playing
    private static bool _loadInGameWhenScanned;
    private static float _hostLoadUntil = -1;
    // Guest: loading the host's world, which itself changes the map; let that through for a while.
    internal static bool LoadingHostWorld => Time.unscaledTime < _hostLoadUntil;
    // The map the host's save led to is up: from here on a map change on a guest is the guest's own try.
    internal static void HostWorldArrived() => _hostLoadUntil = -1;

    public static bool HasWorld { get; private set; }

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.World) OnWorld(r); };
        session.PlayerJoined += id => PendingJoiners.Add(id);
        session.PlayerLeft += id =>
        {
            PendingJoiners.Remove(id);
            if (session.Role == Role.Client && id == 0) CoopDriver.Waiting = null;
            if (session.Role == Role.Client && id == 0 && !session.Left && !session.WasRejected)
                DialogueSync.ShowLocal("Lost the connection to the host. Trying to reconnect; nothing here is saved meanwhile.");
        };
    }

    // Host: the folder of the save that is loaded now (or the newest on disk), minus the thumbnail.
    private static readonly Dictionary<int, string> SentDir = new();

    // Host: a save was loaded. A guest who joined while the host was still at the menu got the newest save on disk,
    // which may not be the one the host picked.
    internal static void OnHostLoadedSave()
    {
        var session = CoopDriver.Session;
        if (session?.Role != Role.Host || !session.IsConnected) return;
        var dir = CurrentSaveDir();
        foreach (var p in session.Players.Values)
            if (p.Ready && SentDir.TryGetValue(p.PlayerId, out var sent) && !string.Equals(Path.GetFullPath(sent), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogInfo($"World transfer: player {p.PlayerId} has {sent}, but the host loaded {dir}; sending it");
                SendWorld(session, p.PlayerId);
            }
    }

    private static void SendWorld(CoopSession session, int playerId, bool newest = false)
    {
        var dir = newest ? NewestSaveDir() : CurrentSaveDir();
        if (dir != null) SentDir[playerId] = dir;
        if (dir == null)
        {
            Plugin.Logger.LogWarning("World transfer: no save to send");
            return;
        }
        var w = session.BeginMessage(MsgType.World);
        w.Put((byte)Kind.Save);
        w.Put(Path.GetFileName(Path.GetDirectoryName(dir)));
        w.Put(Path.GetFileName(dir));
        var files = Array.FindAll(Directory.GetFiles(dir), f => !Path.GetFileName(f).StartsWith("Thumbnail", StringComparison.OrdinalIgnoreCase));
        w.Put(files.Length);
        long total = 0;
        foreach (var f in files)
        {
            var gz = Gzip(File.ReadAllBytes(f));
            total += gz.Length;
            w.Put(Path.GetFileName(f));
            w.Put(gz.Length);  // PutBytesWithLength caps at 64 KB; map files are bigger
            w.Put(gz);
        }
        session.SendTo(playerId, w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"World transfer: sent {dir} ({files.Length} files, {total / 1024} KB) to player {playerId}");
    }

    // Host: the name of the world (save folder) being played, e.g. "- 1 -"; null if none.
    internal static string CurrentWorldName()
    {
        var dir = CurrentSaveDir();
        return string.IsNullOrEmpty(dir) ? null : Path.GetFileName(Path.GetDirectoryName(dir.TrimEnd('\\', '/')));
    }

    private static string CurrentSaveDir()
    {
        var fileIO = FileIO.InstanceIfExists;
        var slg = RekaCoop.Sync.Find.One<SaveLoadGame>();
        if (fileIO != null && slg != null)
        {
            var path = fileIO.GetSaveFilePath(slg.currentSaveGameFolder, slg.currentSaveGameSlot);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        return NewestSaveDir();
    }

    private static string NewestSaveDir()
    {
        string best = null;
        var newest = DateTime.MinValue;
        foreach (var meta in Directory.GetFiles(FileIO.SaveFilesRootPath, "Meta.xml", SearchOption.AllDirectories))
        {
            var t = File.GetLastWriteTimeUtc(meta);
            if (t > newest) { newest = t; best = Path.GetDirectoryName(meta); }
        }
        return best;
    }

    private static void OnWorld(NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Travelling)
        {
            Plugin.Logger.LogInfo("World transfer: the host is travelling to another map");
            InventorySync.ResetLocal();  // stop reporting the pack until the new world hands ours back
            DialogueSync.ShowLocal("The hut is travelling on. Following the host...");
            return;
        }
        if (kind == Kind.Wait)
        {
            Plugin.Logger.LogInfo("World transfer: the host is starting a new journey; waiting");
            DialogueSync.ShowLocal("The host is starting a new journey. You'll join once they're through the fog.");
            CoopDriver.Waiting = "The host is starting a new journey. You'll join once they're through the fog...";
            return;
        }
        _received = r.GetRemainingBytes();
        // No menu can also mean the game is still starting; only a world we already loaded counts as playing.
        _inGameLoad = HasWorld && !MenuWatch.Up;
        Plugin.Logger.LogInfo($"World transfer: received {_received.Length / 1024} KB{(_inGameLoad ? " while playing" : "")}");
    }

    private enum Kind : byte { Save = 1, Travelling = 2, Wait = 3 }

    // Guest: a load we started ourselves (the host's world); any other load from the menu is the player's own.
    internal static bool OurLoad { get; private set; }

    // Host: playing a loaded world, not at the menu or still loading. Only then is there a world to send (and friends
    // can join at all: CoopDriver.WatchHosting).
    internal static bool HostInWorld =>
        PlayerSync.InWorld && !SaveLoadGame.isLoading && !SaveLoadGame.worldIsLoading
        && !MenuWatch.Up;



    // Host, new game: from the start of the intro (character creation, the walk through the fog) until the new world's
    // first save, there is nothing to join; guests are held and told why, then sent the new world.
    private static bool _introRunning;
    private static DateTime _introStartedAt = DateTime.MinValue;
    private static readonly HashSet<int> Told = new();
    private static float _nextJourneyCheck;
    internal static bool StartingJourney => _introStartedAt != DateTime.MinValue;  // cleared once the first save is complete

    internal static void OnIntro(bool running)
    {
        if (running && !_introRunning) _introStartedAt = DateTime.UtcNow;
        _introRunning = running;
        Plugin.Logger.LogInfo($"World transfer: new game intro {(running ? "started; guests wait" : "over")}");
    }

    // A save written since the intro began, finished: the game isn't writing and nothing in it changed for 2 s.
    private static bool NewSaveSinceIntro()
    {
        if (FileIO.isSaving) return false;
        var dir = NewestSaveDir();
        if (dir == null || File.GetLastWriteTimeUtc(Path.Combine(dir, "Meta.xml")) <= _introStartedAt) return false;
        return (DateTime.UtcNow - Directory.GetFiles(dir).Max(f => File.GetLastWriteTimeUtc(f))).TotalSeconds > 2;
    }

    // Once the new world has its first save: everyone connected gets it (guests who were in the old world too).
    private static void WatchJourneyStart(CoopSession session)
    {
        if (_introStartedAt == DateTime.MinValue || _introRunning || Time.unscaledTime < _nextJourneyCheck) return;
        _nextJourneyCheck = Time.unscaledTime + 1f;
        if (MenuWatch.Up)
        {
            // Back at the menu without finishing the intro: no new journey after all.
            _introStartedAt = DateTime.MinValue;
            Plugin.Logger.LogInfo("World transfer: host left the new game intro for the menu");
            return;
        }
        if (!NewSaveSinceIntro()) return;
        _introStartedAt = DateTime.MinValue;
        Plugin.Logger.LogInfo("World transfer: the host's new journey has its first save; guests can join");
        foreach (var p in session.Players.Values.ToList())
            if (p.Ready && !PendingJoiners.Contains(p.PlayerId)) SendWorld(session, p.PlayerId, newest: true);
        foreach (var id in PendingJoiners) SendWorld(session, id, newest: true);
        PendingJoiners.Clear();
        Told.Clear();
    }

    private static void SendWait(CoopSession session, int playerId)
    {
        var w = session.BeginMessage(MsgType.World);
        w.Put((byte)Kind.Wait);
        session.SendTo(playerId, w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"World transfer: player {playerId} waits for the host's new journey");
    }

    // Host: the map is about to change.
    internal static void OnHostTravel(string how)
    {
        var session = CoopDriver.Session;
        if (session?.Role != Role.Host || !session.IsConnected) return;
        // Loading a save can resume a journey that was under way; the guests load the same save, not a new one.
        if (SaveLoadGame.worldIsLoading) { Plugin.Logger.LogInfo($"World transfer: map change ({how}) is part of loading a save"); return; }
        _resendAfterSave = true;
        _travelledAt = DateTime.UtcNow;
        var w = session.BeginMessage(MsgType.World);
        w.Put((byte)Kind.Travelling);
        session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"World transfer: host travels ({how}); the save made on arrival goes to everyone");
    }

    // Host: after a journey, the first save written once it has settled is the new world.
    private static void WatchForArrivalSave(CoopSession session)
    {
        if (!_resendAfterSave || Time.unscaledTime < _nextSaveCheck) return;
        _nextSaveCheck = Time.unscaledTime + 1f;
        var dir = NewestSaveDir();
        if (dir == null) return;
        var written = Directory.GetFiles(dir).Max(f => File.GetLastWriteTimeUtc(f));
        if (written < _travelledAt || DateTime.UtcNow - written < TimeSpan.FromSeconds(3)) return;
        _resendAfterSave = false;
        Plugin.Logger.LogInfo($"World transfer: arrived; sending the new world {dir}");
        foreach (var p in session.Players.Values)
            if (p.Ready) SendWorld(session, p.PlayerId, newest: true);
    }

    // Guest, every frame: once the main menu is up, swap the sandbox saves for the host's world and load it.
    private static int _noSaveTries;

    public static void Tick(CoopSession session)
    {
        if (session.Role == Role.Host)
        {
            WatchForArrivalSave(session);
            WatchJourneyStart(session);
            if (PendingJoiners.Count == 0) return;
            if (StartingJourney)
            {
                foreach (var id in PendingJoiners.Where(id => !Told.Contains(id)).ToList()) { Told.Add(id); SendWait(session, id); }
                return;
            }
            if (!HostInWorld) return;  // mid-load or travelling: the world goes out once it's settled
            // In a world the loaded save is known (SaveLoadGame's folder and slot) even while the menu's rescan of the
            // save list is still running (seen after coming back from the menu and loading at once).
            foreach (var id in PendingJoiners) SendWorld(session, id);
            PendingJoiners.Clear();
            Told.Clear();
            return;
        }
        if (session.Role != Role.Client) return;
        if (_received != null && _inGameLoad)
        {
            // Playing: write the new world and load it through the in-game loader.
            if (!Write(_received)) return;
            _received = null;
            _inGameLoad = false;
            _loadInGameWhenScanned = true;
            _readySince = -1;
            FileIO.InstanceIfExists?.Rescan();
            return;
        }
        if (_loadInGameWhenScanned)
        {
            var fio = FileIO.InstanceIfExists;
            if (fio == null || fio.status != FileIO.Status.Loaded) { _readySince = -1; return; }
            if (_readySince < 0) _readySince = Time.unscaledTime;
            if (Time.unscaledTime - _readySince < 1f) return;
            _loadInGameWhenScanned = false;
            if (!fio.TryGetMostRecentSave(out SaveGame next, out int nf, out int ns))
            {
                Plugin.Logger.LogWarning("World transfer: host world written but the game found no save");
                return;
            }
            MoveToGamePath(fio, nf, ns);
            Plugin.Logger.LogInfo($"World transfer: loading the host's new world '{next.userDefinedName}' (folder {nf} slot {ns})");
            _hostLoadUntil = Time.unscaledTime + 120f;
            PlayerSync.ResetWorld();
            var slg = RekaCoop.Sync.Find.One<SaveLoadGame>();
            if (slg == null) { Plugin.Logger.LogWarning("World transfer: no SaveLoadGame to load the host's world"); return; }
            CoopDriver.Waiting = null;
            slg.LoadSaveGame(nf, ns);
            return;
        }
        if (_received == null && !_loadWhenReady) return;
        var menuNow = MenuWatch.Current;
        // Only from the main menu: in a world, the loaded save must not be swapped underneath the game.
        if (_received != null && menuNow != null && !menuNow.isLoading && Write(_received))
        {
            _received = null;
            HasWorld = true;
            _loadWhenReady = true;
            FileIO.InstanceIfExists?.Rescan();
        }
        if (!_loadWhenReady) return;

        var fileIO = FileIO.InstanceIfExists;
        var menu = MenuWatch.Current;
        bool ready = fileIO != null && fileIO.status == FileIO.Status.Loaded && menu != null && !menu.isLoading;
        if (!ready) { _readySince = -1; return; }
        if (_readySince < 0) _readySince = Time.unscaledTime;
        if (Time.unscaledTime - _readySince < 2f) return;  // let the rescan and menu fade settle

        _loadWhenReady = false;
        if (!fileIO.TryGetMostRecentSave(out SaveGame save, out int folder, out int slot))
        {
            // The rescan may not have started yet when the status still read Loaded from the last scan.
            if (++_noSaveTries <= 5)
            {
                Plugin.Logger.LogInfo($"World transfer: host world not listed yet; rescanning (try {_noSaveTries})");
                _loadWhenReady = true;
                _readySince = -1;
                fileIO.Rescan();
                return;
            }
            Plugin.Logger.LogWarning("World transfer: host world written but the game found no save");
            return;
        }
        _noSaveTries = 0;
        MoveToGamePath(fileIO, folder, slot);
        Plugin.Logger.LogInfo($"World transfer: loading the host's world '{save.userDefinedName}'");
        _hostLoadUntil = Time.unscaledTime + 120f;
        CoopDriver.Waiting = null;
        OurLoad = true;
        try { menu.LoadSaveGame(folder, slot); }
        finally { OurLoad = false; }
    }

    private static bool Write(byte[] body)
    {
        if (!Instance.IsIsolated) return false;  // never outside the sandbox
        var root = FileIO.SaveFilesRootPath;
        if (string.IsNullOrEmpty(root)) return false;  // the game hasn't set up its saves yet
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Instance.SaveRoot), StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Logger.LogError($"World transfer: save root {root} is not the sandbox {Instance.SaveRoot}; not writing");
            return false;
        }
        var r = new NetDataReader(body);
        string folderName = Safe(r.GetString()), slotName = Safe(r.GetString());
        // The host's folder may be a duplicate like "- 1 - (1)", which the guest's save scan doesn't list; the guest
        // only ever holds this one world, so it always goes in the plain first folder.
        folderName = "- 1 -";
        foreach (var d in Directory.GetDirectories(root))
            if (!FileIO.IsBlueprintLibraryFolder(d)) Directory.Delete(d, true);
        var dir = Path.Combine(root, folderName, slotName);
        Directory.CreateDirectory(dir);
        int n = r.GetInt();
        for (int i = 0; i < n; i++)
        {
            string name = Safe(r.GetString());
            var gz = new byte[r.GetInt()];
            r.GetBytes(gz, gz.Length);
            File.WriteAllBytes(Path.Combine(dir, name), Gunzip(gz));
        }
        Plugin.Logger.LogInfo($"World transfer: wrote {n} files to {dir}");
        _writtenDir = dir;
        return true;
    }

    private static string _writtenDir;

    // The game's save list can still be the scan from before the write (seen on build 26298851: it named the guest's old
    // slot, which the write had removed), and loading a slot whose folder is gone starts the tutorial. Whatever slot it
    // picked, put the host's files where that slot's folder is.
    private static void MoveToGamePath(FileIO fileIO, int folder, int slot)
    {
        var expected = fileIO.GetSaveFilePath(folder, slot);
        if (string.IsNullOrEmpty(expected) || _writtenDir == null || !Directory.Exists(_writtenDir)) return;
        expected = Path.GetFullPath(expected.TrimEnd('\\', '/'));
        var written = Path.GetFullPath(_writtenDir);
        if (string.Equals(expected, written, StringComparison.OrdinalIgnoreCase) || Directory.Exists(expected)) return;
        if (!expected.StartsWith(Path.GetFullPath(Instance.SaveRoot), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(expected));
        Directory.Move(written, expected);
        _writtenDir = expected;
        Plugin.Logger.LogInfo($"World transfer: moved the host's world to {expected}, where the game looks for slot {slot}");
    }

    private static string Safe(string name) => Path.GetFileName(name);  // no paths from the network

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}

[HarmonyPatch]
internal static class TravelHooks
{
    [HarmonyPostfix, HarmonyPatch(typeof(CanvasManager), nameof(CanvasManager.ForbidPauseScreen_NewGameIntro))]
    private static void IntroStarted() { if (CoopDriver.Session?.Role != Role.Client) WorldTransfer.OnIntro(true); }

    [HarmonyPostfix, HarmonyPatch(typeof(CanvasManager), nameof(CanvasManager.AllowPauseScreen_NewGameIntro))]
    private static void IntroOver() { if (CoopDriver.Session?.Role != Role.Client) WorldTransfer.OnIntro(false); }

    [HarmonyPostfix, HarmonyPatch(typeof(MapChanger), nameof(MapChanger.FinishGenJump))]
    private static void ArrivedGenerated() { Arrived("generated map"); WorldTransfer.HostWorldArrived(); }

    [HarmonyPostfix, HarmonyPatch(typeof(MapChanger), nameof(MapChanger.FinishHandPlacedJump))]
    private static void ArrivedHandPlaced() { Arrived("hand-placed map"); WorldTransfer.HostWorldArrived(); }

    [HarmonyPostfix, HarmonyPatch(typeof(SaveLoadGame), nameof(SaveLoadGame.ApplyLoadedSaveGame))]
    private static void Loaded()
    {
        Arrived("loaded save");
        WorldTransfer.OnHostLoadedSave();
    }

    private static void Arrived(string what)
    {
        Plugin.Logger.LogInfo($"World transfer: new world ({what}); per-world sync starts over");
        PlayerSync.ResetWorld();
    }

    // Host: about to leave for another map.
    [HarmonyPrefix, HarmonyPatch(typeof(MapChanger), nameof(MapChanger.JumpToNewMap))]
    private static bool NewMap() => Travel("new map");

    [HarmonyPrefix, HarmonyPatch(typeof(MapChanger), nameof(MapChanger.JumpToSameMap))]
    private static bool SameMap() => Travel("same map again");

    [HarmonyPrefix, HarmonyPatch(typeof(MapChanger), nameof(MapChanger.JumpToMap))]
    private static bool HandPlaced() => Travel("hand-placed map");

    // Guests don't travel on their own: they follow the host's save.
    private static bool Travel(string how)
    {
        Plugin.Logger.LogInfo($"World transfer: map change ({how}); isLoading={SaveLoadGame.isLoading} worldIsLoading={SaveLoadGame.worldIsLoading}");
        if (WorldSync.IsGuest && !WorldTransfer.LoadingHostWorld)
        {
            Plugin.Logger.LogInfo($"World transfer: guest map change ({how}) skipped; the host decides where the hut goes");
            return false;
        }
        WorldTransfer.OnHostTravel(how);
        return true;
    }
}

// The count of maps travelled goes up on entering one. A guest enters by loading the save the host made on arrival,
// which already has the new number, and would count the same map again.
[HarmonyPatch(typeof(PoiMapHistoryTracker), nameof(PoiMapHistoryTracker.OnEnteredMap))]
internal static class GuestCountsNoMaps
{
    private static bool Prefix() => !WorldSync.IsGuest;
}

// A guest plays only the host's world: its own Start, Continue, New game and save slots do nothing but say so.
[HarmonyPatch]
internal static class GuestMenuWaits
{
    private static bool Blocked()
    {
        if (CoopDriver.Session?.Role != Role.Client || WorldTransfer.OurLoad) return false;
        CoopDriver.Notice("You're joining a friend's world: it loads by itself in a moment.");
        Plugin.Logger.LogInfo("World transfer: guest's own load from the menu blocked");
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MainMenu), nameof(MainMenu.LoadSaveGame))]
    private static bool Load() => !Blocked();

    [HarmonyPrefix, HarmonyPatch(typeof(SaveGameSlotUiController), nameof(SaveGameSlotUiController.OnStartPressedInMainMenu))]
    private static bool Start() => !Blocked();

    [HarmonyPrefix, HarmonyPatch(typeof(SaveGameSlotUiController), nameof(SaveGameSlotUiController.OnStartNewPressedInMainMenu))]
    private static bool StartNew() => !Blocked();

    [HarmonyPrefix, HarmonyPatch(typeof(SaveGameSlotUiController), nameof(SaveGameSlotUiController.OnContinuePressedInMainMenu))]
    private static bool Continue() => !Blocked();
}

// Guests play in the host's world; only the host writes it.
[HarmonyPatch(typeof(SaveLoadGame))]
internal static class GuestNeverSaves
{
    [HarmonyPrefix, HarmonyPatch(nameof(SaveLoadGame.SaveCurrentSaveGame))]
    private static bool SaveCurrentSaveGame() => !WorldSync.IsGuest && !WorldSync.HostsWorldLoaded;

    [HarmonyPrefix, HarmonyPatch(nameof(SaveLoadGame.DoSpecialSave))]
    private static bool DoSpecialSave() => !WorldSync.IsGuest && !WorldSync.HostsWorldLoaded;
}

// A guest's pause menu shows Save greyed out (it would do nothing: the host saves the shared world).
[HarmonyPatch(typeof(PauseMenu))]
internal static class GuestSaveButton
{
    // DisableSaveButton refreshes the button itself, which lands back here: once only (it overflowed the stack).
    private static bool _inside;

    [HarmonyPostfix, HarmonyPatch(nameof(PauseMenu.OnEnable))]
    private static void Enabled(PauseMenu __instance) => Grey(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(PauseMenu.RefreshSaveButtonState))]
    private static void Refreshed(PauseMenu __instance) => Grey(__instance);

    private static void Grey(PauseMenu menu)
    {
        if (_inside || !WorldSync.IsGuest) return;
        _inside = true;
        try { menu.DisableSaveButton(true); }
        finally { _inside = false; }
    }
}
