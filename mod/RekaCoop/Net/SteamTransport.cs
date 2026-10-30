using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

namespace RekaCoop.Net;

public delegate void WireMessage(ulong from, ReadOnlySpan<byte> data);

// Connectionless messages to and from Steam IDs. SteamWire is the real one; the tests use an in-memory fake.
public interface IMessageWire
{
    void Open(bool acceptAnyone);                               // throws if unavailable
    void Allow(ulong steamId);                                  // may open a session with us
    void Send(ulong to, byte[] data, int length, bool reliable);
    void Receive(WireMessage onMessage);  // drains what has arrived
    void Close(ulong steamId);
}

// ITransport over Steam relay: peers are addressed by Steam ID, no ports or NAT. The wire has no connections, so this
// adds a small handshake (Connect / Accept), a goodbye that carries the farewell, a silence timeout, and splitting of
// big reliable messages (Steam caps one at 512 KB). Every message starts with a tag byte. conn ids are small ints.
public class SteamTransport : ITransport
{
    private enum Tag : byte { Data = 1, Part = 2, Connect = 3, Accept = 4, Bye = 5, Probe = 6 }

    internal const int MaxPart = 256 * 1024;
    private const double ConnectEvery = 1;

    private class Peer
    {
        public int Conn;
        public ulong SteamId;
        public bool Up;
        public ulong Session;  // the guest's game run; a new one from the same Steam ID is a restarted game
        public bool LeftLobby;
        public double LastHeard;
        public List<byte> Partial;
    }

    private readonly IMessageWire _wire;
    private readonly Dictionary<ulong, Peer> _bySteam = new();
    private readonly Dictionary<int, Peer> _byConn = new();
    private readonly List<(ulong id, double at)> _closing = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly NetDataReader _reader = new();
    private byte[] _buf = new byte[1024];
    private bool _open, _listening;
    private ulong _connectTo;
    private readonly ulong _session = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
    private double _nextConnect;
    private int _nextConn = 1;

    // Long: pings come from the other side's main thread, which stops while it loads a map. (UDP keeps a connection
    // alive from LiteNetLib's own thread, so it never sees this.)
    public double Timeout = 60;
    public double LeftLobbyTimeout = 5;  // a guest Steam says left the lobby and who has gone quiet: their game is gone
    private double _lastPoll = -1;

    public event Action<int> Connected;
    public event Action<int, string, NetDataReader> Disconnected;
    public event Action<int, NetDataReader> Received;

    public SteamTransport(IMessageWire wire = null) => _wire = wire ?? new SteamWire();

    private double Now => _clock.Elapsed.TotalSeconds;

    public void Listen(int port)
    {
        Open(true);
        _listening = true;
    }

    // Host: someone joined the lobby. Sending first opens our side of the session even if Steam's session-request
    // callback never reaches us.
    // Host: Steam says this guest left the lobby. A quit or crash rarely gets its goodbye out, so don't wait the full
    // timeout; but the lobby can also drop someone whose game is still talking to us, so only once they go quiet too.
    public void LeftLobby(ulong steamId)
    {
        if (_bySteam.TryGetValue(steamId, out var p)) p.LeftLobby = true;
    }

    public void Expect(ulong steamId)
    {
        if (!_open) return;
        _wire.Allow(steamId);
        SendRaw(steamId, Tag.Probe, default, true);
    }

    // address: the host's Steam ID (decimal). port is unused.
    public void Connect(string address, int port)
    {
        Open(false);
        _connectTo = ulong.Parse(address);
        _wire.Allow(_connectTo);
        _nextConnect = 0;
    }

    private void Open(bool acceptAnyone)
    {
        if (_open) return;
        _wire.Open(acceptAnyone);
        _open = true;
    }

    public void Poll()
    {
        if (!_open) return;
        double now = Now;
        // We were frozen ourselves (loading): that silence isn't the other side's.
        if (_lastPoll >= 0 && now - _lastPoll > 2)
            foreach (var p in _byConn.Values) p.LastHeard += now - _lastPoll;
        _lastPoll = now;
        if (_connectTo != 0 && !_bySteam.ContainsKey(_connectTo) && now >= _nextConnect)
        {
            _nextConnect = now + ConnectEvery;
            SendRaw(_connectTo, Tag.Connect, BitConverter.GetBytes(_session), true);
        }
        _wire.Receive(OnMessage);
        foreach (var p in new List<Peer>(_byConn.Values))
            if (p.Up && now - p.LastHeard > Timeout) Drop(p, "Timeout", null);
            else if (p.Up && p.LeftLobby && now - p.LastHeard > LeftLobbyTimeout) Drop(p, "LeftLobby", null);
        for (int i = _closing.Count - 1; i >= 0; i--)
            if (now >= _closing[i].at)
            {
                if (!_bySteam.ContainsKey(_closing[i].id)) _wire.Close(_closing[i].id);  // not if they're back already
                _closing.RemoveAt(i);
            }
    }

    private void OnMessage(ulong from, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var tag = (Tag)data[0];
        var body = data[1..];
        _bySteam.TryGetValue(from, out var p);
        if (p != null) p.LastHeard = Now;
        switch (tag)
        {
            case Tag.Connect when _listening:
                // A game that crashed or was closed and is back before its old connection timed out: that old
                // player is gone (their body would stay behind otherwise). Repeats of the same Connect are fine.
                ulong session = body.Length >= 8 ? BitConverter.ToUInt64(body) : 0;
                if (p is { Up: true } && session != 0 && p.Session != 0 && session != p.Session)
                {
                    Drop(p, "Rejoined", null);
                    p = null;
                }
                if (p == null) { p = Add(from); p.Session = session; }
                SendRaw(from, Tag.Accept, default, true);
                Up(p);
                break;
            case Tag.Accept when from == _connectTo:
                Up(p ?? Add(from));
                break;
            case Tag.Bye when p != null:
                Drop(p, "RemoteConnectionClose", body.Length > 0 ? Reader(body) : null);
                break;
            case Tag.Part when p is { Up: true }:
                (p.Partial ??= new List<byte>()).AddRange(body.ToArray());
                break;
            case Tag.Data when p is { Up: true }:
                if (p.Partial != null)
                {
                    p.Partial.AddRange(body.ToArray());
                    var whole = p.Partial.ToArray();
                    p.Partial = null;
                    Received?.Invoke(p.Conn, Reader(whole));
                }
                else Received?.Invoke(p.Conn, Reader(body));
                break;
        }
    }

    private void Up(Peer p)
    {
        if (p.Up) return;
        p.Up = true;
        Connected?.Invoke(p.Conn);
    }

    private NetDataReader Reader(ReadOnlySpan<byte> bytes)
    {
        _reader.SetSource(bytes.ToArray());
        return _reader;
    }

    private Peer Add(ulong steamId)
    {
        var p = new Peer { Conn = _nextConn++, SteamId = steamId, LastHeard = Now };
        _bySteam[steamId] = p;
        _byConn[p.Conn] = p;
        return p;
    }

    private void Drop(Peer p, string reason, NetDataReader farewell)
    {
        _bySteam.Remove(p.SteamId);
        _byConn.Remove(p.Conn);
        _closing.Add((p.SteamId, Now + 1));  // let the goodbye go out first
        if (p.Up) Disconnected?.Invoke(p.Conn, reason, farewell);
    }

    public void Send(int conn, NetDataWriter w, DeliveryMethod method)
    {
        if (!_byConn.TryGetValue(conn, out var p) || !p.Up) return;
        var data = new ReadOnlySpan<byte>(w.Data, 0, w.Length);
        if (method is DeliveryMethod.Unreliable or DeliveryMethod.Sequenced) { SendRaw(p.SteamId, Tag.Data, data, false); return; }
        while (data.Length > MaxPart)
        {
            SendRaw(p.SteamId, Tag.Part, data[..MaxPart], true);
            data = data[MaxPart..];
        }
        SendRaw(p.SteamId, Tag.Data, data, true);
    }

    public void Disconnect(int conn, NetDataWriter farewell = null)
    {
        if (!_byConn.TryGetValue(conn, out var p)) return;
        if (p.SteamId == _connectTo) _connectTo = 0;  // stop knocking; the session calls Connect again to retry
        SendRaw(p.SteamId, Tag.Bye, farewell != null ? new ReadOnlySpan<byte>(farewell.Data, 0, farewell.Length) : default, true);
        Drop(p, "DisconnectPeerCalled", null);
    }

    public void Stop()
    {
        if (!_open) return;
        // Sessions close on the next polls (if any): closing now would throw away the goodbyes just queued.
        foreach (var p in new List<Peer>(_byConn.Values)) Disconnect(p.Conn);
        _connectTo = 0;
        _listening = false;
    }

    private void SendRaw(ulong to, Tag tag, ReadOnlySpan<byte> body, bool reliable)
    {
        if (_buf.Length < body.Length + 1) _buf = new byte[body.Length + 1];
        _buf[0] = (byte)tag;
        body.CopyTo(_buf.AsSpan(1));
        _wire.Send(to, _buf, body.Length + 1, reliable);
    }
}
