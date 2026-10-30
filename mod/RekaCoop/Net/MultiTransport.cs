using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace RekaCoop.Net;

// Several transports as one, so a host can take guests over UDP and Steam at the same time. A transport can join
// later (Steam comes up after the game starts). conn = slot * Stride + the transport's own conn.
public class MultiTransport : ITransport
{
    private const int Stride = 1 << 20;
    private readonly List<ITransport> _parts = new();
    private int _listenPort = -1;

    public event Action<int> Connected;
    public event Action<int, string, NetDataReader> Disconnected;
    public event Action<int, NetDataReader> Received;

    public MultiTransport(params ITransport[] parts) { foreach (var t in parts) Add(t); }

    public T Find<T>() where T : class, ITransport
    {
        foreach (var t in _parts) if (t is T found) return found;
        return null;
    }

    public void Add(ITransport t)
    {
        int slot = _parts.Count;
        _parts.Add(t);
        t.Connected += c => Connected?.Invoke(slot * Stride + c);
        t.Disconnected += (c, reason, farewell) => Disconnected?.Invoke(slot * Stride + c, reason, farewell);
        t.Received += (c, r) => Received?.Invoke(slot * Stride + c, r);
        if (_listenPort >= 0) t.Listen(_listenPort);
    }

    public void Listen(int port)
    {
        _listenPort = port;
        Exception failed = null;
        foreach (var t in _parts)
        {
            try { t.Listen(port); }
            catch (Exception e) { failed ??= e; }
        }
        if (failed != null) throw failed;
    }

    // A client connects through the first transport only; it has one host.
    public void Connect(string address, int port) => _parts[0].Connect(address, port);

    public void Poll() { foreach (var t in _parts) t.Poll(); }

    public void Send(int conn, NetDataWriter w, DeliveryMethod method) => _parts[conn / Stride].Send(conn % Stride, w, method);

    public void Disconnect(int conn, NetDataWriter farewell = null) => _parts[conn / Stride].Disconnect(conn % Stride, farewell);

    public void Stop() { foreach (var t in _parts) t.Stop(); }
}
