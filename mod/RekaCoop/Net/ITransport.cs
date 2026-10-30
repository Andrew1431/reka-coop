using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace RekaCoop.Net;

// What CoopSession needs from a network: numbered connections that carry byte messages. UdpTransport (LiteNetLib,
// LAN / IP) and SteamTransport (Steam relay, by Steam ID) implement it. All events fire from Poll(), on its thread.
public interface ITransport
{
    void Listen(int port);                    // host
    void Connect(string address, int port);   // client; may be called again to retry while not connected
    void Poll();
    void Send(int conn, NetDataWriter w, DeliveryMethod method);
    void Disconnect(int conn, NetDataWriter farewell = null);  // farewell reaches the other side's Disconnected
    void Stop();

    event Action<int> Connected;
    event Action<int, string, NetDataReader> Disconnected;  // conn, reason, farewell (or null)
    event Action<int, NetDataReader> Received;
}
