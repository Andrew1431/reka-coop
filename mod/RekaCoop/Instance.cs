using System;
using System.IO;
using RekaCoop.Net;

namespace RekaCoop;

// Identity of this game process, from the command line. Players use none of these: with the mod installed the game
// hosts its world over Steam once it's in one, and accepting a friend's invite makes it a guest (Auto).
// The rest are for development and joining by IP:
//   -coopinstance <name>    dev test copy: own save root inside its game folder, never the real saves
//                           (a guest without it uses CoopSaves/guest)
//   -coophost [port]        host a session
//   -coopconnect host:port  join a session over UDP
//   -coopguest              join over Steam: wait for an invite (accepting one in game then works)
//   +connect_lobby <id>     added by Steam when it starts the game from an invite or "Join Game": join that lobby
internal static class Instance
{
    public static string Name { get; private set; }
    public static Role Role { get; private set; }
    public static int Port { get; private set; } = Protocol.DefaultPort;
    public static string ConnectAddress { get; private set; }  // null for a Steam guest
    public static ulong LobbyToJoin { get; private set; }

    // No co-op options on the command line: host over Steam when in a world, become a guest on an invite, and back.
    public static bool Auto { get; private set; }

    internal static void BecomeGuest() { if (Auto) Role = Role.Client; }
    internal static void BecomeHost() { if (Auto) { Role = Role.Host; LobbyToJoin = 0; } }

    // A guest always plays in a sandbox: the host's world is copied there, and the guest's own saves are never touched.
    public static bool IsIsolated => Name != null || Role == Role.Client;

    public static string SaveRoot => Path.Combine(BepInEx.Paths.GameRootPath, "CoopSaves", Name ?? "guest");

    public static void Init()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "-coopinstance" when next != null:
                    Name = next;
                    break;
                case "-coophost":
                    Role = Role.Host;
                    if (int.TryParse(next, out int port)) Port = port;
                    break;
                case "-coopguest":
                    Role = Role.Client;
                    break;
                case "+connect_lobby" when ParseLobby(next) is ulong lobby:
                    LobbyToJoin = lobby;  // joined once Steam is up (Auto: becomes a guest then)
                    break;
                case "-coopconnect" when next != null:
                    Role = Role.Client;
                    int colon = next.LastIndexOf(':');
                    ConnectAddress = colon > 0 ? next[..colon] : next;
                    if (colon > 0 && int.TryParse(next[(colon + 1)..], out int p)) Port = p;
                    break;
            }
        }
        if (Role == Role.None)
        {
            Auto = true;
            Role = Role.Host;
        }
    }

    // A lobby id, alone or after +connect_lobby (Steam's rich-presence "connect" string).
    public static ulong? ParseLobby(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.StartsWith("+connect_lobby", StringComparison.OrdinalIgnoreCase)) s = s["+connect_lobby".Length..].Trim();
        return ulong.TryParse(s, out ulong id) && id != 0 ? id : null;
    }
}
