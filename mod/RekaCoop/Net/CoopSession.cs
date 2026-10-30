using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

namespace RekaCoop.Net;

public enum Role { None, Host, Client }

public class RemotePlayer
{
    public int Conn;        // transport connection
    public int PlayerId;
    public string Name;
    public bool Ready;      // handshake finished
    public double RttMs;
}

// Host-authoritative session over an ITransport. No Unity references: Poll() is driven from the main thread.
public class CoopSession
{
    public Role Role { get; private set; }
    public string LocalName { get; set; }  // sent in Hello; may be filled in once Steam is up
    public int LocalPlayerId { get; private set; }  // host is 0
    public string State { get; private set; } = "offline";
    public readonly Dictionary<int, RemotePlayer> Players = new();  // keyed by connection

    public event Action<string> Log;

    private readonly ITransport _net;
    private readonly NetDataWriter _w = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _nextPlayerId = 1;
    private double _nextPing;

    public double Now => _clock.Elapsed.TotalSeconds;

    public CoopSession(string localName, ITransport transport = null)
    {
        LocalName = localName;
        _net = transport ?? new UdpTransport();
        _net.Connected += OnPeerConnected;
        _net.Disconnected += OnPeerDisconnected;
        _net.Received += OnReceive;
    }

    // A transport that can't listen (port taken) throws, but the session still hosts on the ones that can.
    public void Host(int port)
    {
        Role = Role.Host;
        LocalPlayerId = 0;
        _net.Listen(port);
        State = port > 0 ? $"hosting on {port}" : "hosting";
        Log?.Invoke(State);
    }

    // Host: whether new players are let in. Closed while the host has no world to share (at the main menu).
    public bool Open = true;

    // Host: everyone out, with a reason their game shows.
    public void Close(string reason)
    {
        foreach (var p in new List<RemotePlayer>(Players.Values))
        {
            var w = Begin(MsgType.Reject);
            w.Put(reason);
            _net.Disconnect(p.Conn, w);
        }
        Log?.Invoke($"closed: {reason}");
    }

    // Client: the host turned us away or ended the session (the reason is for the player).
    public event Action<string> Rejected;

    private string _address;
    private int _port;
    private double _retryAt;
    private bool _rejected;
    public bool WasRejected => _rejected;

    // Client: keeps trying every few seconds while not connected (the host may not be up yet, or restarted).
    public void Connect(string address, int port)
    {
        Role = Role.Client;
        Left = false;
        _rejected = false;
        _address = address;
        _port = port;
        _retryAt = Now + RetryEvery;
        _net.Connect(address, port);
        State = $"connecting to {address}:{port}";
        Log?.Invoke(State);
    }

    private const double RetryEvery = 5.0;
    public double HelloTimeout = 15;  // client: connected but no answer to Hello this long -> drop and retry
    private double _connectedAt = -1;

    public void Poll()
    {
        _net.Poll();
        if (Role == Role.Client && _connectedAt >= 0 && !IsConnected && Now - _connectedAt > HelloTimeout)
        {
            Log?.Invoke("the host doesn't answer; reconnecting");
            _connectedAt = -1;
            foreach (var p in new List<RemotePlayer>(Players.Values)) _net.Disconnect(p.Conn);
        }
        if (Role == Role.Client && !_rejected && !Left && Players.Count == 0 && Now >= _retryAt)
        {
            _retryAt = Now + RetryEvery;
            _net.Connect(_address, _port);
            State = $"connecting to {_address}:{_port}";
        }
        if (Now >= _nextPing)
        {
            _nextPing = Now + 1.0;
            foreach (var p in Players.Values)
                if (p.Ready) { var w = Begin(MsgType.Ping); w.Put(Now); _net.Send(p.Conn, w, DeliveryMethod.Unreliable); }
        }
    }

    public void Stop()
    {
        if (Role == Role.Host) Close("The host closed Reka.");
        _net.Stop();
    }

    // Tests: the game gone without a goodbye (a crash); guests keep retrying.
    public void StopWithoutGoodbye() => _net.Stop();

    // Client: done playing with this host (back at the main menu). Says goodbye and stops retrying; Connect rejoins.
    public bool Left { get; private set; }
    public void Leave()
    {
        if (Role != Role.Client) return;
        Left = true;
        _connectedAt = -1;
        foreach (var p in new List<RemotePlayer>(Players.Values)) _net.Disconnect(p.Conn);
        State = "left the host";
        Log?.Invoke(State);
    }

    // Host: drop a player (e.g. an old connection of someone who has already rejoined).
    public void Kick(int playerId)
    {
        foreach (var p in Players.Values)
            if (p.PlayerId == playerId && p.Ready) { Log?.Invoke($"dropping player {playerId}"); _net.Disconnect(p.Conn); return; }
    }

    public bool IsConnected
    {
        get
        {
            foreach (var p in Players.Values) if (p.Ready) return true;
            return false;
        }
    }

    // Game messages (MsgType >= FirstGameMessage). Sender is the peer it came from: on a client that is always the host.
    public event Action<RemotePlayer, MsgType, NetDataReader> Message;
    public event Action<int> PlayerLeft;
    public event Action<int> PlayerJoined;  // host: a client finished the handshake

    public void SendTo(int playerId, NetDataWriter w, DeliveryMethod method)
    {
        foreach (var p in Players.Values)
            if (p.Ready && p.PlayerId == playerId) _net.Send(p.Conn, w, method);
    }

    public NetDataWriter BeginMessage(MsgType type) => Begin(type);

    // Client: to the host. Host: to every joined client except `exceptPlayerId`.
    public void Send(NetDataWriter w, DeliveryMethod method, int exceptPlayerId = -1)
    {
        foreach (var p in Players.Values)
            if (p.Ready && p.PlayerId != exceptPlayerId) _net.Send(p.Conn, w, method);
    }

    private NetDataWriter Begin(MsgType type)
    {
        _w.Reset();
        _w.Put((byte)type);
        return _w;
    }

    private void OnPeerConnected(int conn)
    {
        Players[conn] = new RemotePlayer { Conn = conn };
        Log?.Invoke($"peer connected {conn}");
        if (Role == Role.Client) _connectedAt = Now;
        if (Role == Role.Client)
            { var w = Begin(MsgType.Hello); w.Put(Protocol.Version); w.Put(LocalName); _net.Send(conn, w, DeliveryMethod.ReliableOrdered); }
    }

    private void OnPeerDisconnected(int conn, string reason, NetDataReader farewell)
    {
        Players.Remove(conn, out var gone);
        if (Role == Role.Client) _connectedAt = -1;
        Log?.Invoke($"peer disconnected {conn}: {reason}");
        if (Role == Role.Client)
        {
            State = $"disconnected ({reason})";
            if (farewell is { AvailableBytes: > 1 } d && (MsgType)d.GetByte() == MsgType.Reject)
                OnRejected(d.GetString());
            if (gone is { Ready: true }) PlayerLeft?.Invoke(0);  // not on each failed retry
        }
        else if (gone is { Ready: true })
        {
            PlayerLeft?.Invoke(gone.PlayerId);
            var w = Begin(MsgType.PlayerLeft);
            w.Put(gone.PlayerId);
            Send(w, DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnRejected(string reason)
    {
        _rejected = true;
        State = $"rejected: {reason}";
        Log?.Invoke(State);
        if (Role == Role.Client) _net.Stop();  // the Steam transport would otherwise keep knocking and get back in
        Rejected?.Invoke(reason);
    }

    private void OnReceive(int conn, NetDataReader r)
    {
        if (!Players.TryGetValue(conn, out var player)) return;
        var type = (MsgType)r.GetByte();
        switch (type)
        {
            case MsgType.Hello when Role == Role.Host:
            {
                int version = r.GetInt();
                string name = r.GetString();
                if (!Open)
                {
                    // Not a refusal: the guest keeps retrying and gets in once the host is in a world.
                    Log?.Invoke($"'{name}' tried to join while closed; dropped for now");
                    _net.Disconnect(conn);
                    return;
                }
                if (version != Protocol.Version)
                {
                    // In the disconnect packet: a reliable message sent just before Disconnect() is dropped.
                    var w = Begin(MsgType.Reject);
                    w.Put($"protocol {version} != host {Protocol.Version}");
                    _net.Disconnect(conn, w);
                    return;
                }
                player.Name = name;
                player.PlayerId = _nextPlayerId++;
                player.Ready = true;
                { var w = Begin(MsgType.HelloAck); w.Put(player.PlayerId); _net.Send(conn, w, DeliveryMethod.ReliableOrdered); }
                Log?.Invoke($"player {player.PlayerId} '{name}' joined");
                PlayerJoined?.Invoke(player.PlayerId);
                break;
            }
            case MsgType.HelloAck when Role == Role.Client:
                LocalPlayerId = r.GetInt();
                player.Name = "host";
                player.Ready = true;
                State = $"connected as player {LocalPlayerId}";
                Log?.Invoke(State);
                break;
            case MsgType.Reject:
                OnRejected(r.GetString());
                break;
            case MsgType.Ping:
                { var w = Begin(MsgType.Pong); w.Put(r.GetDouble()); _net.Send(conn, w, DeliveryMethod.Unreliable); }
                break;
            case MsgType.Pong:
                player.RttMs = (Now - r.GetDouble()) * 1000.0;
                break;
            case MsgType.PlayerLeft when Role == Role.Client:
                PlayerLeft?.Invoke(r.GetInt());
                break;
            default:
                if (player.Ready && type >= MsgType.FirstGameMessage) Message?.Invoke(player, type, r);
                break;
        }
    }
}
