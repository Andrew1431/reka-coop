using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The rest of the story's memory: what the game's own save components (ISaveable) hold about progress, such as which
// tools and skills are unlocked (the ravens, the pine branch), recipes and items known, and the
// collection's rewards. The story runs on the host, so the host's are the truth. Each component is captured on its own,
// as the game would save it, into an otherwise empty save; when one changes the host sends it, and a guest restores it
// on its own copy of that component, the way loading a save would. Components other syncs cover (the pack, trials,
// flags, the house) or that belong to each player (position, look, play time) are left out.
public static class SaveableSync
{
    private enum Kind : byte { Ready = 1, State = 2 }

    // Types whose saved state is shared progress.
    private static readonly HashSet<string> Shared = new()
    {
        "UseItem_None", "UseItem_Lantern", "UseItem_PineBranch", "UseItem_WateringCan", "UseItem_SvarogCharm", "UseItem_Birds",
        "UseItem_ConsumablePotion", "UseItem_EnchantedLarchCone", "UseItem_Backport", "UseItem_BrokenDziadyMask",
        "UseItem_BuildMode", "UseItem_HouseBuildingMode", "UseItem_GardenMode", "UseItem_Fire",
        "Tutorial_DisableBabaDuringTut", "DeactivateHomeForTutorial", "PlayerProgress", "OvenUnlocker",
        "BuildingQuestStarter", "BuildingRuleHints", "CollectionRewardsManager", "SaveLoadCollection", "BB_Building_NPCComments",
        "PlayerGuidance", "PoiMapHistoryTracker",
    };

    private sealed class Entry { public string Key; public ISaveable Saveable; public MonoBehaviour Owner; }

    private static List<Entry> _entries;          // found in this world
    private static SaveableSceneWalk _walk;        // finding them, a few ms per frame
    private static readonly Dictionary<string, string> Sent = new();     // host: as last sent
    private static readonly Dictionary<string, string> Applied = new();  // guest: as last applied
    private static string _empty;
    private static int _next;
    private static float _nextRound;
    private static bool _readySent;
    private static readonly HashSet<int> Waiting = new();  // host: guests that asked before our list was ready
    internal static bool Applying;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Saveable) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _entries = null;
        _walk = null;
        Sent.Clear();
        Applied.Clear();
        OwnBase.Clear();
        _hostStates = false;
        _nextOwn = 0;
        _readySent = false;
        _next = 0;
    }

    private static SaveGame NewSave() => new()
    {
        saveableInventory = new SaveGame.SaveableInventory(), saveableProgress = new SaveGame.SaveableProgress(),
        saveableStats = new SaveGame.SaveableStats(), saveablePoiHistory = new SaveGame.SaveablePoiHistory(),
        saveablePlayerGuidance = new SaveGame.SaveablePlayerGuidance(), saveableCollection = new SaveGame.SaveableCollection(),
        savedBBs = new SaveLoadBBs.SavedBBs(),
    };

    private static string Serialize(SaveGame sg) =>
        string.Concat(
            ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveableProgress>(), sg.saveableProgress), "\u0001",
            ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveablePlayerGuidance>(), sg.saveablePlayerGuidance), "\u0001",
            ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveableCollection>(), sg.saveableCollection), "\u0001",
            ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveablePoiHistory>(), sg.saveablePoiHistory));

    private static SaveGame Deserialize(string xml)
    {
        var parts = xml.Split('\u0001');
        var sg = NewSave();
        sg.saveableProgress = ClassicElementStateBridge.Deserialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveableProgress>(), parts[0]).Cast<SaveGame.SaveableProgress>();
        sg.saveablePlayerGuidance = ClassicElementStateBridge.Deserialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveablePlayerGuidance>(), parts[1]).Cast<SaveGame.SaveablePlayerGuidance>();
        sg.saveableCollection = ClassicElementStateBridge.Deserialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveableCollection>(), parts[2]).Cast<SaveGame.SaveableCollection>();
        sg.saveablePoiHistory = ClassicElementStateBridge.Deserialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveGame.SaveablePoiHistory>(), parts[3]).Cast<SaveGame.SaveablePoiHistory>();
        return sg;
    }

    private static string Capture(Entry e)
    {
        if (e.Owner == null) return null;
        try
        {
            var sg = NewSave();
            e.Saveable.GetStateForSaving(sg);
            var xml = Serialize(sg);
            return xml == (_empty ??= Serialize(NewSave())) ? null : xml;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Saveables: can't capture {e.Key}: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    // Walks the scene for ISaveables across frames, as the game does when saving; true once the list is ready.
    private static bool Found()
    {
        if (_entries != null) return true;
        if (_walk == null)
        {
            _walk = new SaveableSceneWalk();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                _walk.PushRoots(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i));
            _walk.PushRoots(SaveableSceneWalk.DontDestroyOnLoadScene);
            return false;
        }
        if (!_walk.Step(2.0)) return false;
        _walk.FillLists();
        var seen = new HashSet<IntPtr>();
        var byType = new Dictionary<string, List<(string path, ISaveable s, MonoBehaviour mb)>>();
        foreach (var s in _walk.saveables)
        {
            var mb = s.TryCast<MonoBehaviour>();
            if (mb == null || !seen.Add(mb.Pointer)) continue;
            var type = mb.GetIl2CppType().Name;
            if (!Shared.Contains(type)) continue;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = new();
            list.Add((ScenePath.Of(mb.transform), s, mb));
        }
        _entries = new List<Entry>();
        foreach (var (type, list) in byType)
            foreach (var g in list.GroupBy(x => x.path))
            {
                int n = 0;
                foreach (var x in g) _entries.Add(new Entry { Key = $"{type}@{x.path}#{n++}", Saveable = x.s, Owner = x.mb });
            }
        _walk = null;
        Plugin.Logger.LogInfo($"Saveables: {_entries.Count} shared ({string.Join(", ", byType.Select(kv => $"{kv.Key} {kv.Value.Count}"))})");
        return true;
    }

    // Called once the local body is in the world.
    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || SaveLoadGame.isLoading || !Found()) return;
        if (session.Role == Role.Client)
        {
            if (!_readySent)
            {
                _readySent = true;
                var w = session.BeginMessage(MsgType.Saveable);
                w.Put((byte)Kind.Ready);
                session.Send(w, DeliveryMethod.ReliableOrdered);
                return;
            }
            TickOwnUnlocks(session);
            return;
        }
        foreach (var p in Waiting) SendAll(session, p);
        Waiting.Clear();
        // One component a frame, each every 3 s (a capture serializes several KB).
        if (_entries.Count == 0) return;
        if (_next >= _entries.Count)
        {
            if (Time.unscaledTime < _nextRound) return;
            _next = 0;
            _nextRound = Time.unscaledTime + 3f;
        }
        var e = _entries[_next++];
        var xml = Capture(e);
        if (xml == null) return;
        if (!Sent.TryGetValue(e.Key, out var was)) { Sent[e.Key] = xml; return; }  // the guest got it with the world
        if (was == xml) return;
        Sent[e.Key] = xml;
        Send(session, new List<(string, string)> { (e.Key, xml) }, -1);
        Plugin.Logger.LogInfo($"Saveables: sent {e.Key}");
    }

    // Guest: a tool or skill the guest's own story unlocked (the talisman's charm) goes to the host, which restores it
    // on its copy and passes it on. Only the tools: the other components hold whole lists the host's copy may be ahead in.
    private static int _nextOwn;
    private static float _nextOwnRound;
    private static readonly Dictionary<string, string> OwnBase = new();
    private static bool _hostStates;

    private static void TickOwnUnlocks(CoopSession session)
    {
        if (!_hostStates || _entries.Count == 0) return;
        if (_nextOwn >= _entries.Count)
        {
            if (Time.unscaledTime < _nextOwnRound) return;
            _nextOwn = 0;
            _nextOwnRound = Time.unscaledTime + 1f;
        }
        var e = _entries[_nextOwn++];
        if (!e.Key.StartsWith("UseItem_")) return;
        var xml = Capture(e);
        if (xml == null) return;
        var was = Applied.TryGetValue(e.Key, out var a) ? a : OwnBase.GetValueOrDefault(e.Key);
        if (was == null) { OwnBase[e.Key] = xml; return; }
        if (was == xml) return;
        Applied[e.Key] = xml;
        Send(session, new List<(string, string)> { (e.Key, xml) }, -1);
        Plugin.Logger.LogInfo($"Saveables: sent our {e.Key} to the host");
    }

    private static void Send(CoopSession session, List<(string key, string xml)> states, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Saveable);
        w.Put((byte)Kind.State);
        w.Put(states.Count);
        foreach (var (key, xml) in states) { w.Put(key); var gz = Pack(xml); w.Put(gz.Length); w.Put(gz); }  // PutBytesWithLength caps at 64 KB
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            if (_entries == null) Waiting.Add(from.PlayerId);  // ours are still being found
            else SendAll(session, from.PlayerId);
        }
        else if (kind == Kind.State && session.Role == Role.Client)
        {
            int n = r.GetInt(), changed = 0;
            for (int i = 0; i < n; i++)
            {
                var key = r.GetString();
                var gz = new byte[r.GetInt()];
                r.GetBytes(gz, gz.Length);
                var xml = Unpack(gz);
                if (Apply(key, xml)) changed++;
            }
            _hostStates = true;
            Plugin.Logger.LogInfo($"Saveables: host's {n}, {changed} changed here");
        }
        else if (kind == Kind.State && session.Role == Role.Host)
        {
            // A guest's own unlock; our next round sees the change and sends it to everyone.
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                var key = r.GetString();
                var gz = new byte[r.GetInt()];
                r.GetBytes(gz, gz.Length);
                if (!key.StartsWith("UseItem_")) continue;
                if (Apply(key, Unpack(gz))) Plugin.Logger.LogInfo($"Saveables: took on player {from.PlayerId}'s {key}");
            }
        }
    }

    private static void SendAll(CoopSession session, int player)
    {
        var all = new List<(string, string)>();
        foreach (var e in _entries) if (Capture(e) is string xml) { all.Add((e.Key, xml)); Sent[e.Key] = xml; }
        Send(session, all, player);
        Plugin.Logger.LogInfo($"Saveables: sent all {all.Count} to player {player}");
    }

    private static byte[] Pack(string s)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(s);
        using var ms = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string Unpack(byte[] data)
    {
        using var gz = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var sr = new System.IO.StreamReader(gz, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static bool Apply(string key, string xml)
    {
        var e = _entries?.FirstOrDefault(x => x.Key == key);
        if (e == null) { Plugin.Logger.LogInfo($"Saveables: no {key} here"); return false; }
        if (Capture(e) == xml) { Applied[key] = xml; return false; }
        Applying = true;
        try
        {
            e.Saveable.RestoreSavedState(Deserialize(xml));
            // The place history's restore leaves out which map this is (the game counts it up on entering a map, which
            // a guest taking the host's world doesn't do), so a guest would file its visits under map 0.
            if (key.StartsWith("PoiMapHistoryTracker") && System.Text.RegularExpressions.Regex.Match(xml, @"<currentMapNumber>(-?\d+)</currentMapNumber>") is { Success: true } m
                && RekaCoop.Sync.Find.One<PoiMapHistoryTracker>() is { } history)
                history.currentMapNumber = int.Parse(m.Groups[1].Value);
            Applied[key] = Capture(e) ?? xml;  // as it reads back here, so our own-unlock check doesn't send it back
            Plugin.Logger.LogInfo($"Saveables: restored {key}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Saveables: can't restore {key}: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
        finally { Applying = false; }
    }

    // For DigestSync: each shared component's saved state, hashed.
    internal static IEnumerable<KeyValuePair<string, string>> Snapshot() => _entries == null ? null : Walk(_entries);

    private static IEnumerable<KeyValuePair<string, string>> Walk(List<Entry> entries)
    {
        foreach (var e in entries)
            if (Capture(e) is string xml) yield return new(e.Key, DigestSync.Hash(xml).ToString("x16"));
    }

    // Dev: one component's captured state, readable.
    internal static string Dump(string typePart) =>
        _entries == null ? "not found yet" : string.Join("\n\n", _entries.Where(e => e.Key.Contains(typePart)).Select(e => e.Key + "\n" + (Capture(e) ?? "(empty)").Replace("\u0001", "\n----\n")));

    public static string Describe() =>
        _entries == null ? "not found yet" : string.Join("; ", _entries.Select(e => $"{e.Key} {(Capture(e) is string x ? x.Length : 0)}"));
}
