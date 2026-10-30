using System;
using System.Collections.Generic;
using RekaCoop.Net;

namespace RekaCoop.Tests;

// An in-memory Steam: every wire has a Steam ID and an inbox. Messages are delivered whole and in order (reliable
// ones always; unreliable ones too, here). A wire that is Gone silently drops everything, like a crashed game.
internal class FakeSteam
{
    private readonly Dictionary<ulong, Wire> _wires = new();
    public int LargestMessage;

    public Wire Make(ulong steamId) => _wires[steamId] = new Wire(this, steamId);

    internal class Wire : IMessageWire
    {
        private readonly FakeSteam _net;
        public readonly ulong Id;
        public readonly Queue<(ulong from, byte[] data)> Inbox = new();
        public bool Gone, Opened;

        public Wire(FakeSteam net, ulong id) { _net = net; Id = id; }

        public void Open(bool acceptAnyone) => Opened = true;
        public void Allow(ulong steamId) { }
        // Like Steam: closing a session drops what is still queued from that peer.
        public void Close(ulong steamId)
        {
            var keep = new Queue<(ulong, byte[])>();
            foreach (var m in Inbox) if (m.Item1 != steamId) keep.Enqueue(m);
            Inbox.Clear();
            foreach (var m in keep) Inbox.Enqueue(m);
            Closed++;
        }

        public int Closed;

        public void Send(ulong to, byte[] data, int length, bool reliable)
        {
            if (length > 512 * 1024) throw new Exception($"Steam refuses a {length} byte message");
            _net.LargestMessage = Math.Max(_net.LargestMessage, length);
            if (Gone || !_net._wires.TryGetValue(to, out var w) || w.Gone || !w.Opened) return;
            w.Inbox.Enqueue((Id, data.AsSpan(0, length).ToArray()));
        }

        public void Receive(WireMessage onMessage)
        {
            while (!Gone && Inbox.Count > 0)
            {
                var (from, data) = Inbox.Dequeue();
                onMessage(from, data);
            }
        }
    }
}
