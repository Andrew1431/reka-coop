using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace RekaCoop.Net;

// LiteNetLib over UDP: LAN, loopback dev copies, or the internet with a forwarded port. conn = NetPeer.Id.
public class UdpTransport : ITransport
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly System.Collections.Generic.Dictionary<int, NetPeer> _peers = new();
    private bool _listening;

    public event Action<int> Connected;
    public event Action<int, string, NetDataReader> Disconnected;
    public event Action<int, NetDataReader> Received;

    public UdpTransport()
    {
        _net = new NetManager(_listener) { DisconnectTimeout = 20000, AutoRecycle = true };
        _listener.ConnectionRequestEvent += r => { if (_listening) r.AcceptIfKey(Protocol.ConnectKey); else r.Reject(); };
        _listener.PeerConnectedEvent += p => { _peers[p.Id] = p; Connected?.Invoke(p.Id); };
        _listener.PeerDisconnectedEvent += (p, info) =>
        {
            _peers.Remove(p.Id);
            Disconnected?.Invoke(p.Id, info.Reason.ToString(), info.AdditionalData is { AvailableBytes: > 0 } d ? d : null);
        };
        _listener.NetworkReceiveEvent += (p, r, _, _) => Received?.Invoke(p.Id, r);
    }

    public void Listen(int port)
    {
        if (!_net.Start(port)) throw new InvalidOperationException($"Could not listen on UDP {port}");
        _listening = true;
    }

    public void Connect(string address, int port)
    {
        if (!_net.IsRunning) _net.Start();
        _net.Connect(address, port, Protocol.ConnectKey);
    }

    public void Poll() => _net.PollEvents();

    public void Send(int conn, NetDataWriter w, DeliveryMethod method)
    {
        if (_peers.TryGetValue(conn, out var p)) p.Send(w, method);
    }

    public void Disconnect(int conn, NetDataWriter farewell = null)
    {
        if (!_peers.TryGetValue(conn, out var p)) return;
        if (farewell != null) p.Disconnect(farewell);
        else p.Disconnect();
    }

    public void Stop() => _net.Stop();
}
