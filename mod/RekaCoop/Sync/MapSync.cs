using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Map and compass highlights (a quest's places, a place picked on the map) are shared: whatever anyone highlights or
// clears shows on everyone's map. The game keeps them on CompassLogic's elements by ID; each copy is watched for
// changes (whatever made them: the quest page, the map, a popup's Highlight button) and the change is sent on.
public static class MapSync
{
    private enum Kind : byte { Ready = 1, Change = 2, All = 3 }

    private const float PollEvery = 0.5f;

    private static HashSet<string> _seen;  // highlighted here, as last looked at or set from others
    private static float _nextPoll;
    private static bool _readySent;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Map) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _seen = null;
        _readySent = false;
    }

    private static HashSet<string> Highlighted()
    {
        var set = new HashSet<string>();
        var all = CompassLogic.allElements;
        if (all == null) return set;
        foreach (var e in all)
            if (e != null && e.isHighlighted && !string.IsNullOrEmpty(e.ID)) set.Add(e.ID);
        return set;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollEvery;
        if (CompassLogic.allElements == null) return;
        if (session.Role == Role.Client && !_readySent)
        {
            _readySent = true;
            var rw = session.BeginMessage(MsgType.Map);
            rw.Put((byte)Kind.Ready);
            session.Send(rw, DeliveryMethod.ReliableOrdered);
        }
        var now = Highlighted();
        if (_seen == null) { _seen = now; return; }
        var changes = new List<(string id, bool on)>();
        foreach (var id in now) if (!_seen.Contains(id)) changes.Add((id, true));
        foreach (var id in _seen) if (!now.Contains(id)) changes.Add((id, false));
        _seen = now;
        if (changes.Count == 0) return;
        var w = session.BeginMessage(MsgType.Map);
        w.Put((byte)Kind.Change);
        w.Put(changes.Count);
        foreach (var (id, on) in changes) { w.Put(id); w.Put(on); }
        session.Send(w, DeliveryMethod.ReliableOrdered);
        foreach (var (id, on) in changes) Plugin.Logger.LogInfo($"Map: {(on ? "highlighted" : "cleared")} {id} here; sent");
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        bool ok = CompassLogic.allElements != null;
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            if (!ok) return;
            var all = Highlighted();
            var w = session.BeginMessage(MsgType.Map);
            w.Put((byte)Kind.All);
            w.Put(all.Count);
            foreach (var id in all) w.Put(id);
            session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            return;
        }
        var changes = new List<(string id, bool on)>();
        int n = r.GetInt();
        if (kind == Kind.Change)
            for (int i = 0; i < n; i++) changes.Add((r.GetString(), r.GetBool()));
        else if (kind == Kind.All)
        {
            var theirs = new HashSet<string>();
            for (int i = 0; i < n; i++) theirs.Add(r.GetString());
            if (ok)
            {
                foreach (var id in theirs) changes.Add((id, true));
                foreach (var id in Highlighted()) if (!theirs.Contains(id)) changes.Add((id, false));
            }
        }
        else return;
        if (session.Role == Role.Host && kind == Kind.Change)
        {
            var w = session.BeginMessage(MsgType.Map);
            w.Put((byte)Kind.Change);
            w.Put(changes.Count);
            foreach (var (id, on) in changes) { w.Put(id); w.Put(on); }
            session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
        }
        if (!ok) return;
        foreach (var (id, on) in changes)
        {
            try { CompassLogic.SetHighlight(id, on); }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"Map: highlighting {id} failed: {e.Message}"); continue; }
            Plugin.Logger.LogInfo($"Map: {NameTags.NameOf(from.PlayerId)} {(on ? "highlighted" : "cleared")} {id}");
        }
        // What the game made of it (an ID it doesn't know is left out) is what this copy has now; not a change to send.
        _seen = Highlighted();
    }

    public static string Describe()
    {
        var all = CompassLogic.allElements;
        var sb = new System.Text.StringBuilder($"{all?.Count ?? 0} elements, highlighted:");
        foreach (var id in Highlighted()) sb.Append(' ').Append(id);
        sb.Append("; first IDs:");
        if (all != null) for (int i = 0; i < all.Count && i < 15; i++) sb.Append(' ').Append(all[i]?.ID);
        return sb.ToString();
    }
}
