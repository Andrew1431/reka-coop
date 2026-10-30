using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Sends the local player's gear when it changes; shows everyone else's on their puppets. The host keeps the
// latest gear of each player and replays it to anyone who joins later, like looks.
public static class GearSync
{
    private const float CheckEvery = 0.25f;

    private static readonly Dictionary<int, GearState> Latest = new();
    private static Dictionary<string, GameObject> _local;
    private static byte[] _lastSent;
    private static float _nextCheck;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Gear) OnGear(session, from, r); };
        session.PlayerLeft += id => { if (session.Role == Role.Client && id == 0) _lastSent = null; };  // resend after reconnecting
        session.PlayerJoined += id =>
        {
            foreach (var (playerId, gear) in Latest) Send(session, playerId, gear, toPlayer: id);
        };
        session.PlayerLeft += id => Latest.Remove(id);
    }

    public static GearState StateOf(int playerId) => Latest.TryGetValue(playerId, out var g) ? g : null;

    // A new world or a new local body: find the gear objects again and resend.
    public static void ResetLocal()
    {
        _local = null;
        _lastSent = null;
    }

    public static void Tick(CoopSession session, PlayerMovement_2021 local)
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + CheckEvery;
        _local ??= GearObjects.Find(local);

        var state = new GearState();
        foreach (var (key, go) in _local)
            if (go != null) state.Entries.Add(GearObjects.Capture(key, go));

        // Compare a coarse copy so follower drag and float noise don't resend every check.
        var coarse = new NetDataWriter();
        foreach (var e in state.Entries)
        {
            coarse.Put(e.Key); coarse.Put(e.Active); coarse.Put(e.Anchor); coarse.Put(e.Children); coarse.Put(e.Effects);
            foreach (var f in e.Lights) coarse.Put(Mathf.RoundToInt(Mathf.Log(1 + Mathf.Max(0, f)) * 10));  // about 10% brightness steps
            coarse.Put(Mathf.RoundToInt(e.Pos.x * 200)); coarse.Put(Mathf.RoundToInt(e.Pos.y * 200)); coarse.Put(Mathf.RoundToInt(e.Pos.z * 200));
            var euler = e.Rot.eulerAngles;
            coarse.Put(Mathf.RoundToInt(euler.x / 5)); coarse.Put(Mathf.RoundToInt(euler.y / 5)); coarse.Put(Mathf.RoundToInt(euler.z / 5));
        }
        var bytes = coarse.CopyData();
        if (_lastSent != null && bytes.SequenceEqual(_lastSent)) return;
        _lastSent = bytes;

        Latest[session.LocalPlayerId] = state;
        if (session.IsConnected) Send(session, session.LocalPlayerId, state);
    }

    private static void OnGear(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        int playerId = r.GetInt();
        if (session.Role == Role.Host) playerId = from.PlayerId;  // clients only speak for themselves
        var state = GearState.Read(r);
        if (playerId == session.LocalPlayerId) return;
        Latest[playerId] = state;
        if (session.Role == Role.Host) Send(session, playerId, state, exceptPlayerId: playerId);
        PlayerSync.ApplyGear(playerId, state);
    }

    private static void Send(CoopSession session, int playerId, GearState state, int toPlayer = -1, int exceptPlayerId = -1)
    {
        var w = session.BeginMessage(MsgType.Gear);
        w.Put(playerId);
        state.Write(w);
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId);
    }
}
