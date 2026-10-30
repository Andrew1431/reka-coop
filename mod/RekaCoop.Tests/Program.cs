using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;

namespace RekaCoop.Tests;

// Real sessions, polled from this thread the way CoopDriver polls them from Unity's main thread. Session tests run
// twice: over UDP loopback and over SteamTransport on an in-memory Steam.
internal static class Program
{
    private static int _port = 28877;
    private static int _failed;

    // How sessions are made for one run of the tests.
    private abstract class Backend
    {
        public abstract string Name { get; }
        public abstract CoopSession Host();
        public abstract CoopSession Guest(string name);
        public virtual CoopSession RestartHost() => Host();
    }

    private class Udp : Backend
    {
        public override string Name => "udp";
        public override CoopSession Host() { var s = new CoopSession("host"); s.Host(_port); return s; }
        public override CoopSession Guest(string name) { var s = new CoopSession(name); s.Connect("127.0.0.1", _port); return s; }
    }

    private class Steam : Backend
    {
        public const ulong HostId = 7656119800000000;
        public readonly FakeSteam Net = new();
        public FakeSteam.Wire HostWire;
        public SteamTransport HostTransport;
        private ulong _next = HostId + 1;
        public double Timeout = 20;
        public override string Name => "steam";
        public override CoopSession Host()
        {
            HostWire = Net.Make(HostId);
            HostTransport = new SteamTransport(HostWire) { Timeout = Timeout };
            var s = new CoopSession("host", HostTransport);
            s.Host(0);
            return s;
        }
        public override CoopSession Guest(string name) => Guest(name, _next++);
        public CoopSession Guest(string name, ulong steamId)
        {
            var s = new CoopSession(name, new SteamTransport(Net.Make(steamId)) { Timeout = Timeout });
            s.Connect(HostId.ToString(), 0);
            return s;
        }
    }

    private static int Main()
    {
        foreach (var make in new Func<Backend>[] { () => new Udp(), () => new Steam() })
        {
            var name = make().Name;
            Run($"{name}: handshake assigns ids 1..4", () => FourPlayersGetIds(make()));
            Run($"{name}: host relays to everyone but the sender", () => RelaySkipsSender(make()));
            Run($"{name}: guest started before host connects when host comes up", () => GuestBeforeHost(make()));
            Run($"{name}: guest reconnects after host crash and restart", () => HostRestart(make()));
            Run($"{name}: a host that quits ends the session; the guest is told and doesn't retry", () => HostQuits(make()));
            Run($"{name}: kick drops one player and tells the rest", () => Kick(make()));
            Run($"{name}: ping measures rtt", () => Ping(make()));
            Run($"{name}: 1.2 MB reliable message arrives whole", () => BigMessage(make()));
            Run($"{name}: a guest that leaves is gone for the host, stays away, and can join again", () => LeaveAndRejoin(make()));
        }
        Run("udp: version mismatch is rejected and not retried", VersionMismatch);
        Run("udp: a host that accepts but never answers Hello is dropped and retried", SilentHost);
        Run("steam: guest notices a host that vanished without a goodbye", SteamHostVanishes);
        Run("steam: a freeze (map load) on both sides longer than the timeout drops no one", SteamBothFreeze);
        Run("steam: a kicked guest that reconnects at once ends up connected", SteamQuickRejoin);
        Run("steam: a guest whose game crashed and restarts replaces its old self", SteamCrashRestart);
        Run("steam: a guest who left the lobby and went quiet is dropped well before the timeout", SteamLeftLobby);
        Run("steam: a guest who left the lobby but still talks stays", SteamLeftLobbyStillTalking);
        Run("mixed: a host whose UDP port is taken still takes Steam guests", BusyPort);
        Run("mixed: host takes a UDP and a Steam guest (Steam added late) and relays between them", Mixed);
        Run("reports: scrubbing takes out names, user, PC, IPs and Steam ids", Scrub);
        Console.WriteLine(_failed == 0 ? "all passed" : $"{_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _port++;
        var sw = Stopwatch.StartNew();
        try { test(); Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)"); }
        catch (Exception e) { _failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
    }

    private static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    private static void Scrub()
    {
        var s = new RekaCoop.Diagnostics.Scrubber("0123456789abcdef0123456789abcdef", new[] { ("Zofia W", "player 0"), ("Borys", "player 1"), ("client", "player 9") });
        string user = Environment.UserName, pc = Environment.MachineName;
        var o = s.Clean(string.Join("\n",
            $@"Save root -> C:\Users\{user}\code\reka\CoopSaves\play and D:/Users/someone/x",
            "Steam: up as Zofia W; Borys joined the lobby; borys's hut; Borysek stays",
            "lobby 109775242798482770 ready, friend 76561198012345678",
            "connecting to 192.168.1.23:27877, local 127.0.0.1, v6 2001:db8:85a3:0:0:8a2e:370:7334 fe80::1c2d",
            "[12:43:26.123] BepInEx 6.0.0-be.791 mod 0.3.14 day 16 time 7.821",
            $@"\\{pc}\Reka and {pc}",
            "[net] player 1 'client' joined"));
        foreach (var gone in new[] { user, pc, "Zofia", "Borys ", "192.168.1.23", "2001:db8", "fe80::", "109775242798482770", "76561198012345678", "someone" })
            Check(!o.Contains(gone, StringComparison.OrdinalIgnoreCase), $"'{gone}' left in:\n{o}");
        foreach (var kept in new[] { "player 0", "player 1's hut", "Borysek", "127.0.0.1", "12:43:26.123", "6.0.0-be.791", "0.3.14", "'client' joined", "id:" + s.Hash("76561198012345678") })
            Check(o.Contains(kept), $"'{kept}' missing in:\n{o}");
    }

    private static void PollUntil(Func<bool> done, string what, double seconds, params CoopSession[] sessions)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new Exception($"timed out waiting for {what}");
            foreach (var s in sessions) s.Poll();
            Thread.Sleep(5);
        }
    }

    private static void PollFor(double seconds, params CoopSession[] sessions)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { foreach (var s in sessions) s.Poll(); Thread.Sleep(5); }
    }

    private static void Stop(params CoopSession[] sessions) { foreach (var s in sessions) s?.Stop(); }

    private static void FourPlayersGetIds(Backend b)
    {
        var host = b.Host();
        var guests = new[] { b.Guest("a"), b.Guest("b"), b.Guest("c"), b.Guest("d") };
        var all = new List<CoopSession>(guests) { host }.ToArray();
        try
        {
            PollUntil(() => Array.TrueForAll(guests, g => g.IsConnected), "all guests connected", 5, all);
            var ids = new HashSet<int>();
            foreach (var g in guests) ids.Add(g.LocalPlayerId);
            Check(ids.SetEquals(new[] { 1, 2, 3, 4 }), $"ids {string.Join(",", ids)}");
            Check(host.Players.Count == 4, $"host sees {host.Players.Count}");
            foreach (var p in host.Players.Values) Check(p.Ready && p.Name.Length == 1, $"player {p.PlayerId} '{p.Name}' ready={p.Ready}");
        }
        finally { Stop(all); }
    }

    private static void RelaySkipsSender(Backend be)
    {
        var host = be.Host();
        var a = be.Guest("a");
        var b = be.Guest("b");
        var got = new Dictionary<string, int>();
        void Count(string who, MsgType t) { if (t == MsgType.Name) got[who] = got.GetValueOrDefault(who) + 1; }
        a.Message += (_, t, _) => Count("a", t);
        b.Message += (_, t, _) => Count("b", t);
        try
        {
            PollUntil(() => a.IsConnected && b.IsConnected, "guests connected", 5, host, a, b);
            var w = host.BeginMessage(MsgType.Name);
            w.Put(a.LocalPlayerId);
            w.Put("relayed");
            host.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: a.LocalPlayerId);
            PollUntil(() => got.ContainsKey("b"), "b receives", 3, host, a, b);
            PollFor(0.2, host, a, b);
            Check(!got.ContainsKey("a"), "sender got its own message back");
            Check(got["b"] == 1, $"b got {got["b"]}");
        }
        finally { Stop(host, a, b); }
    }

    private static void GuestBeforeHost(Backend b)
    {
        var guest = b.Guest("early");
        try
        {
            PollFor(6, guest);  // longer than one retry interval
            Check(!guest.IsConnected, "connected with no host");
            var host = b.Host();
            try { PollUntil(() => guest.IsConnected, "late host accepts", 12, host, guest); }
            finally { host.Stop(); }
        }
        finally { guest.Stop(); }
    }

    private static void HostRestart(Backend b)
    {
        var host = b.Host();
        var guest = b.Guest("g");
        var left = new List<int>();
        guest.PlayerLeft += left.Add;
        try
        {
            PollUntil(() => guest.IsConnected, "first connect", 5, host, guest);
            host.StopWithoutGoodbye();
            PollUntil(() => !guest.IsConnected, "guest notices", 25, guest);
            Check(left.Count == 1 && left[0] == 0, $"PlayerLeft raised {left.Count} times");
            host = b.RestartHost();
            PollUntil(() => guest.IsConnected, "reconnect", 15, host, guest);
            Check(guest.LocalPlayerId == 1, $"new id {guest.LocalPlayerId}");
            Check(left.Count == 1, $"PlayerLeft raised {left.Count} times while retrying");
        }
        finally { Stop(host, guest); }
    }

    private static void HostQuits(Backend b)
    {
        var host = b.Host();
        var guest = b.Guest("g");
        string reason = null;
        guest.Rejected += r => reason = r;
        try
        {
            PollUntil(() => guest.IsConnected, "first connect", 5, host, guest);
            host.Stop();
            PollUntil(() => reason != null, "guest told", 25, guest);
            Check(guest.WasRejected, "guest still retrying");
            host = b.RestartHost();
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until) { host.Poll(); guest.Poll(); System.Threading.Thread.Sleep(10); }
            Check(!guest.IsConnected, "guest reconnected on its own");
        }
        finally { Stop(host, guest); }
    }

    private static void Kick(Backend be)
    {
        var host = be.Host();
        var a = be.Guest("a");
        var b = be.Guest("b");
        var leftOnB = new List<int>();
        b.PlayerLeft += leftOnB.Add;
        try
        {
            PollUntil(() => a.IsConnected && b.IsConnected, "guests connected", 5, host, a, b);
            int aId = a.LocalPlayerId;
            host.Kick(aId);
            PollUntil(() => !a.IsConnected, "a dropped", 5, host, a, b);
            PollUntil(() => leftOnB.Contains(aId), "b told", 5, host, a, b);
            Check(b.IsConnected, "b dropped too");
        }
        finally { Stop(host, a, b); }
    }

    private static void Ping(Backend b)
    {
        var host = b.Host();
        var g = b.Guest("g");
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            RemotePlayer onHost = null;
            PollUntil(() =>
            {
                foreach (var p in host.Players.Values) onHost = p;
                return onHost is { RttMs: > 0 };
            }, "rtt measured", 5, host, g);
            Check(onHost.RttMs < 500, $"rtt {onHost.RttMs}");
        }
        finally { Stop(host, g); }
    }

    // World transfers and place snapshots run to hundreds of KB.
    private static void BigMessage(Backend b)
    {
        var host = b.Host();
        var g = b.Guest("g");
        byte[] got = null;
        g.Message += (_, t, r) => { if (t == MsgType.World) { got = new byte[r.GetInt()]; r.GetBytes(got, got.Length); } };
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            var data = new byte[1200 * 1024];
            new Random(1).NextBytes(data);
            var w = host.BeginMessage(MsgType.World);
            w.Put(data.Length);
            w.Put(data);
            host.Send(w, DeliveryMethod.ReliableOrdered);
            PollUntil(() => got != null, "big message", 10, host, g);
            Check(got.AsSpan().SequenceEqual(data), "contents differ");
            if (b is Steam s) Check(s.Net.LargestMessage <= SteamTransport.MaxPart + 1, $"sent {s.Net.LargestMessage} bytes at once");
        }
        finally { Stop(host, g); }
    }

    private static void VersionMismatch()
    {
        var host = new Udp().Host();
        var listener = new EventBasedNetListener();
        var raw = new NetManager(listener);
        string rejected = null;
        int connects = 0;
        listener.PeerConnectedEvent += peer =>
        {
            connects++;
            var w = new NetDataWriter();
            w.Put((byte)MsgType.Hello);
            w.Put(Protocol.Version - 1);
            w.Put("old");
            peer.Send(w, DeliveryMethod.ReliableOrdered);
        };
        listener.PeerDisconnectedEvent += (_, i) =>
        {
            if (i.AdditionalData is { AvailableBytes: > 1 } d && (MsgType)d.GetByte() == MsgType.Reject) rejected = d.GetString();
        };
        raw.Start();
        raw.Connect("127.0.0.1", _port, Protocol.ConnectKey);
        try
        {
            var sw = Stopwatch.StartNew();
            while (rejected == null && sw.Elapsed.TotalSeconds < 5) { host.Poll(); raw.PollEvents(); Thread.Sleep(5); }
            Check(rejected != null && rejected.Contains($"{Protocol.Version - 1}"), $"reject reason '{rejected}'");
            Check(!host.IsConnected, "old client counted as joined");
            sw.Restart();
            while (sw.Elapsed.TotalSeconds < 7) { host.Poll(); raw.PollEvents(); Thread.Sleep(5); }
            Check(connects == 1, $"connected {connects} times");
        }
        finally { raw.Stop(); host.Stop(); }
    }

    private static void BusyPort()
    {
        using var squatter = new System.Net.Sockets.UdpClient(_port);  // another copy of the game
        var steam = new Steam();
        var multi = new MultiTransport(new UdpTransport());
        var host = new CoopSession("host", multi);
        bool threw = false;
        try { host.Host(_port); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "listening on a taken port didn't fail");
        steam.HostWire = steam.Net.Make(Steam.HostId);
        multi.Add(new SteamTransport(steam.HostWire));
        var g = steam.Guest("s");
        try { PollUntil(() => g.IsConnected && host.Players.Count == 1, "steam guest joined", 5, host, g); }
        finally { Stop(host, g); }
    }

    private static void Mixed()
    {
        var steam = new Steam();
        var multi = new MultiTransport(new UdpTransport());
        var host = new CoopSession("host", multi);
        host.Host(_port);
        var udpGuest = new Udp().Guest("u");
        PollFor(0.2, host, udpGuest);
        steam.HostWire = steam.Net.Make(Steam.HostId);
        multi.Add(new SteamTransport(steam.HostWire));
        var steamGuest = steam.Guest("s");
        string gotOnUdp = null;
        udpGuest.Message += (_, t, r) => { if (t == MsgType.Name) { r.GetInt(); gotOnUdp = r.GetString(); } };
        host.Message += (from, t, r) =>
        {
            if (t != MsgType.Name) return;
            var w = host.BeginMessage(MsgType.Name);
            w.Put(r.GetInt());
            w.Put(r.GetString());
            host.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
        };
        try
        {
            PollUntil(() => udpGuest.IsConnected && steamGuest.IsConnected, "both connected", 8, host, udpGuest, steamGuest);
            Check(udpGuest.LocalPlayerId != steamGuest.LocalPlayerId, "same player id");
            var w = steamGuest.BeginMessage(MsgType.Name);
            w.Put(steamGuest.LocalPlayerId);
            w.Put("from steam");
            steamGuest.Send(w, DeliveryMethod.ReliableOrdered);
            PollUntil(() => gotOnUdp != null, "relay steam -> udp", 5, host, udpGuest, steamGuest);
            Check(gotOnUdp == "from steam", gotOnUdp);
            host.Kick(steamGuest.LocalPlayerId);
            PollUntil(() => !steamGuest.IsConnected, "steam guest kicked", 5, host, udpGuest, steamGuest);
            Check(udpGuest.IsConnected, "udp guest dropped too");
        }
        finally { Stop(host, udpGuest, steamGuest); }
    }

    private static void SteamQuickRejoin()
    {
        var b = new Steam();
        var host = b.Host();
        var g = b.Guest("g");
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            host.Kick(g.LocalPlayerId);
            PollUntil(() => g.IsConnected && g.LocalPlayerId == 2, "rejoined as player 2", 15, host, g);
            PollFor(2, host, g);  // past the old session's delayed close
            Check(g.IsConnected && host.IsConnected, $"after the delayed close: guest {g.IsConnected}, host {host.IsConnected}");
        }
        finally { Stop(host, g); }
    }

    private static void LeaveAndRejoin(Backend b)
    {
        var host = b.Host();
        var g = b.Guest("g");
        try
        {
            var left = new List<int>();
            host.PlayerLeft += id => left.Add(id);
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            g.Leave();
            PollUntil(() => left.Contains(1), "host sees player 1 leave", 5, host, g);
            PollFor(6.5, host, g);  // past the guest's retry interval
            Check(!g.IsConnected && host.Players.Count == 0, $"came back on its own: guest {g.IsConnected}, host has {host.Players.Count}");
            g.Connect(b is Steam ? Steam.HostId.ToString() : "127.0.0.1", _port);
            PollUntil(() => g.IsConnected && host.Players.Count == 1, "rejoined", 10, host, g);
        }
        finally { Stop(host, g); }
    }

    private static void SteamLeftLobby()
    {
        var b = new Steam { Timeout = 60 };
        var host = b.Host();
        var g = b.Guest("g", 43);
        try
        {
            var left = new List<int>();
            host.PlayerLeft += id => left.Add(id);
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            b.HostTransport.LeftLobby(43);  // quit: Steam drops it from the lobby, the goodbye never comes
            PollUntil(() => left.Contains(1), "dropped after leaving the lobby", 8, host);
        }
        finally { Stop(host); }
    }

    private static void SteamLeftLobbyStillTalking()
    {
        var b = new Steam { Timeout = 60 };
        var host = b.Host();
        var g = b.Guest("g", 44);
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            b.HostTransport.LeftLobby(44);  // Steam hiccup: out of the lobby, but the game is fine
            PollFor(7, host, g);
            Check(g.IsConnected && host.Players.Count == 1, $"dropped a guest that still talks: guest {g.IsConnected}, host has {host.Players.Count}");
        }
        finally { Stop(host, g); }
    }

    private static void SteamCrashRestart()
    {
        var b = new Steam();
        var host = b.Host();
        var g = b.Guest("g", 42);
        CoopSession again = null;
        try
        {
            var left = new List<int>();
            host.PlayerLeft += id => left.Add(id);
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            again = b.Guest("g", 42);  // the old game was killed: it never says goodbye and is never polled again
            PollUntil(() => again.IsConnected, "restarted guest connected", 10, host, again);
            PollFor(1.5, host, again);
            Check(left.Contains(1), $"old player 1 not removed (left: {string.Join(",", left)})");
            Check(host.Players.Count == 1, $"host has {host.Players.Count} players");
        }
        finally { Stop(host, again); }
    }

    private static void SteamBothFreeze()
    {
        var b = new Steam { Timeout = 2 };
        var host = b.Host();
        var g = b.Guest("g");
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            Thread.Sleep(3500);  // neither polls, like two games loading at once
            PollFor(1, host, g);
            Check(g.IsConnected && host.IsConnected, $"dropped: guest {g.IsConnected}, host {host.IsConnected}");
        }
        finally { Stop(host, g); }
    }

    private static void SilentHost()
    {
        var listener = new EventBasedNetListener();
        var raw = new NetManager(listener);
        int connects = 0;
        listener.ConnectionRequestEvent += r => r.AcceptIfKey(Protocol.ConnectKey);
        listener.PeerConnectedEvent += _ => connects++;
        raw.Start(_port);
        var g = new CoopSession("g") { HelloTimeout = 2 };
        g.Connect("127.0.0.1", _port);
        try
        {
            var sw = Stopwatch.StartNew();
            while (connects < 2 && sw.Elapsed.TotalSeconds < 12) { g.Poll(); raw.PollEvents(); Thread.Sleep(5); }
            Check(connects >= 2, $"connected {connects} times");
            Check(!g.IsConnected, "counted as connected");
        }
        finally { g.Stop(); raw.Stop(); }
    }

    private static void SteamHostVanishes()
    {
        var b = new Steam { Timeout = 2 };
        var host = b.Host();
        var g = b.Guest("g");
        var left = new List<int>();
        g.PlayerLeft += left.Add;
        try
        {
            PollUntil(() => g.IsConnected, "connected", 5, host, g);
            b.HostWire.Gone = true;  // crashed: no goodbye
            PollUntil(() => !g.IsConnected, "timeout", 5, g);
            Check(left.Contains(0), "PlayerLeft(0) not raised");
            host = b.Host();
            PollUntil(() => g.IsConnected, "reconnect to new host", 15, host, g);
        }
        finally { Stop(host, g); }
    }
}
