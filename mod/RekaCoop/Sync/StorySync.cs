using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The story's memory lives in four string-keyed tables: game-wide conditions and values, this map's conditions, and
// the dialogue lines heard on this map. The host's tables are the truth: it sends what changed once a second, and
// everything to a guest that has just loaded. A guest's own changes go the other way and the host takes them on.
// Both write them through the game's own setters, so whatever listens to a flag reacts as it would locally.
public static class StorySync
{
    private enum Kind : byte { Ready = 1, Flags = 2 }
    private enum Table : byte { GameCondition = 1, GameValue = 2, MapCondition = 3, Heard = 4 }

    private static Dictionary<(Table, string), int> _sent;   // host: as last sent
    private static float _nextPoll;
    private static bool _readySent;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Story) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _sent = null;
        _readySent = false;
    }

    private static Dictionary<(Table, string), int> Capture()
    {
        var all = new Dictionary<(Table, string), int>();
        if (StaticGameCondition.syncedConditions != null)
            foreach (var kv in StaticGameCondition.syncedConditions) all[(Table.GameCondition, kv.Key)] = kv.Value ? 1 : 0;
        if (StaticGameValue.syncedValues != null)
            foreach (var kv in StaticGameValue.syncedValues) all[(Table.GameValue, kv.Key)] = kv.Value;
        if (StaticMapCondition.syncedConditions != null)
            foreach (var kv in StaticMapCondition.syncedConditions) all[(Table.MapCondition, kv.Key)] = kv.Value ? 1 : 0;
        if (SaveLoad_HeardDialoguePerMap.heardDialogueIDs != null)
            foreach (var id in SaveLoad_HeardDialoguePerMap.heardDialogueIDs) all[(Table.Heard, id)] = 1;
        return all;
    }

    // Called once the local body is in the world.
    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        if (session.Role == Role.Client)
        {
            if (!_readySent)
            {
                _readySent = true;
                var w = session.BeginMessage(MsgType.Story);
                w.Put((byte)Kind.Ready);
                session.Send(w, DeliveryMethod.ReliableOrdered);
                return;
            }
            // What the guest's own story did (a place solved, an item taken) goes to the host, which takes it on and
            // passes it to everyone. The host's flags, as applied, are the base, so they aren't sent back.
            if (Time.unscaledTime < _nextPoll || _sent == null) return;
            _nextPoll = Time.unscaledTime + 1f;
            var mine = Capture().Where(kv => !_sent.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
            foreach (var kv in mine) _sent[kv.Key] = kv.Value;
            if (mine.Count == 0) return;
            Send(session, mine, -1);
            Plugin.Logger.LogInfo($"Story: sent our {mine.Count} changes to the host: {string.Join(", ", mine.Take(5).Select(kv => $"{kv.Key.Item1}:{kv.Key.Item2}={kv.Value}"))}");
            return;
        }
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 1f;
        var now = Capture();
        if (_sent == null) { _sent = now; return; }
        var changed = now.Where(kv => !_sent.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        // Heard lines and map conditions can also be dropped (a new map); send those as 0.
        foreach (var key in _sent.Keys) if (!now.ContainsKey(key)) changed.Add(new KeyValuePair<(Table, string), int>(key, 0));
        _sent = now;
        if (changed.Count == 0) return;
        Send(session, changed, -1);
        Plugin.Logger.LogInfo($"Story: sent {changed.Count} changes: {string.Join(", ", changed.Take(5).Select(kv => $"{kv.Key.Item1}:{kv.Key.Item2}={kv.Value}"))}");
    }

    private static void Send(CoopSession session, List<KeyValuePair<(Table, string), int>> flags, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Story);
        w.Put((byte)Kind.Flags);
        w.Put(flags.Count);
        foreach (var kv in flags) { w.Put((byte)kv.Key.Item1); w.Put(kv.Key.Item2); w.Put(kv.Value); }
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var all = Capture().ToList();
            Send(session, all, from.PlayerId);
            Plugin.Logger.LogInfo($"Story: sent all {all.Count} story flags to player {from.PlayerId}");
        }
        else if (kind == Kind.Flags)
        {
            // Guest: the host's flags. Host: a guest's own changes, taken on here; the next poll sends them to everyone.
            bool host = session.Role == Role.Host;
            int n = r.GetInt(), changed = 0;
            for (int i = 0; i < n; i++)
            {
                var table = (Table)r.GetByte();
                string id = r.GetString();
                int value = r.GetInt();
                if (Apply(table, id, value)) changed++;
            }
            if (!host) _sent = Capture();
            Plugin.Logger.LogInfo(host ? $"Story: player {from.PlayerId}'s flags: {n} received, {changed} taken on" : $"Story: host's flags: {n} received, {changed} changed here");
        }
    }

    private static bool Apply(Table table, string id, int value)
    {
        switch (table)
        {
            case Table.GameCondition:
                if (StaticGameCondition.syncedConditions.TryGetValue(id, out var gc) && gc == (value != 0)) return false;
                StaticGameCondition.SetCondition(id, value != 0);
                return true;
            case Table.GameValue:
                if (StaticGameValue.syncedValues.TryGetValue(id, out var gv) && gv == value) return false;
                StaticGameValue.syncedValues[id] = value;
                StaticGameValue.onChanged?.Invoke(id);
                return true;
            case Table.MapCondition:
                if (StaticMapCondition.syncedConditions.TryGetValue(id, out var mc) && mc == (value != 0)) return false;
                StaticMapCondition.ChangeCondition(id, value != 0);
                return true;
            case Table.Heard:
                if (SaveLoad_HeardDialoguePerMap.HasHeard(id) == (value != 0)) return false;
                SaveLoad_HeardDialoguePerMap.SetHeard(id, value != 0);
                return true;
        }
        return false;
    }

    // For DigestSync: unset and 0 read the same.
    internal static Dictionary<string, string> Snapshot()
    {
        var all = new Dictionary<string, string>();
        foreach (var kv in Capture()) if (kv.Value != 0) all[$"{kv.Key.Item1}:{kv.Key.Item2}"] = kv.Value.ToString();
        return all;
    }

    public static string Describe()
    {
        var all = Capture();
        return $"{all.Count} flags: " + string.Join(", ", all.GroupBy(kv => kv.Key.Item1).Select(g => $"{g.Key} {g.Count()}"));
    }
}
