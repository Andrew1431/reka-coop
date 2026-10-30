using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using RekaCoop.Net;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace RekaCoop.Steam;

// Steam friends join without IPs or ports. While the host is in a world (session open) it has a friends-only lobby and
// takes guests over Steam relay; "Invite" opens Steam's invite overlay. Back at the main menu the lobby goes away.
// A guest joins when Steam started the game from an invite or "Join Game" (+connect_lobby <id>), or when it accepts
// one at the main menu. The lobby is only where the host's Steam ID is found; game traffic goes through SteamTransport.
// Uses the game's own Facepunch.Steamworks; its callbacks are queued and handled in Tick on the main thread.
internal static class SteamLobby
{
    private static bool _started, _creating;
    private static Lobby? _lobby;
    private static CoopSession _session;
    private static SteamTransport _steam;
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly System.Collections.Generic.List<Delegate> Keep = new();  // Il2Cpp holds only pointers
    private static float _joiningSince = -1, _connectingSince = -1;  // guest: to say so when a step hangs
    private const float JoinPatience = 20f, ConnectPatience = 40f;

    public static string Status { get; private set; } = "";
    public static bool CanInvite => _lobby.HasValue && Instance.Role == Role.Host;

    public static void Tick(CoopSession session, MultiTransport net)
    {
        while (Queue.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Steam: {e}"); }
        }
        Watch(session);
        if (_started) KeepLobby(session);
        if (_started || !SteamReady()) return;
        _started = true;
        try { Start(session, net); }
        catch (Exception e)
        {
            Status = "Steam unavailable";
            Plugin.Logger.LogWarning($"Steam: can't start lobbies, IP only: {e}");
        }
    }

    private static void Watch(CoopSession session)
    {
        float now = Time.unscaledTime;
        if (session.IsConnected) { _joiningSince = _connectingSince = -1; return; }
        if (_joiningSince >= 0 && now - _joiningSince > JoinPatience)
        {
            _joiningSince = -1;
            Status = "Steam: couldn't join the lobby";
            Plugin.Logger.LogWarning(Status);
            Sync.DialogueSync.ShowLocal("Couldn't join the host's Steam lobby. Ask for a new invite, or join by IP (-coopconnect).");
        }
        if (_connectingSince >= 0 && now - _connectingSince > ConnectPatience)
        {
            _connectingSince = -1;
            Status = "Steam: in the lobby, but the host doesn't answer";
            Plugin.Logger.LogWarning(Status);
            Sync.DialogueSync.ShowLocal("The host doesn't answer over Steam. Still trying; if it stays like this, join by IP (-coopconnect).");
        }
    }

    private static bool SteamReady()
    {
        try { return SteamClient.IsValid; }
        catch { return false; }
    }

    // Host: a lobby while the session is open (in a world), none otherwise.
    private static void KeepLobby(CoopSession session)
    {
        if (session.Role == Role.Host && session.Open && _lobby == null && !_creating && _steam != null)
        {
            _creating = true;
            SteamMatchmaking.CreateLobbyAsync(4);  // OnLobbyCreated carries the result
            Status = "Steam: making lobby";
        }
        else if (session.Role == Role.Host && !session.Open && _lobby != null)
        {
            Leave();
            Status = "Steam: friends can join once you're in a world";
            Plugin.Logger.LogInfo("Steam: lobby closed (not in a world)");
        }
    }

    private static void Start(CoopSession session, MultiTransport net)
    {
        _session = session;
        On(SteamMatchmaking.add_OnLobbyCreated, new Action<Result, Lobby>((r, l) => Queue.Enqueue(() => OnCreated(r, l))));
        On(SteamMatchmaking.add_OnLobbyEntered, new Action<Lobby>(l => Queue.Enqueue(() => OnEntered(session, l))));
        On(SteamMatchmaking.add_OnLobbyMemberJoined, new Action<Lobby, Friend>((l, f) => Queue.Enqueue(() => OnMemberJoined(f))));
        On(SteamMatchmaking.add_OnLobbyMemberLeave, new Action<Lobby, Friend>((l, f) => Queue.Enqueue(() =>
        {
            Plugin.Logger.LogInfo($"Steam: {f.Name} left the lobby");
            _steam?.LeftLobby(f.Id.Value);
        })));
        On(SteamFriends.add_OnGameLobbyJoinRequested, new Action<Lobby, SteamId>((l, f) => Queue.Enqueue(() => OnJoinRequested(session, l.Id.Value))));
        On(SteamFriends.add_OnGameRichPresenceJoinRequested, new Action<Friend, string>((f, connect) => Queue.Enqueue(() =>
        {
            if (Instance.ParseLobby(connect) is ulong id) OnJoinRequested(session, id);
        })));

        if (Instance.Auto || Instance.Role == Role.Host)
        {
            _steam = net.Find<SteamTransport>();
            if (_steam == null) { _steam = new SteamTransport(); net.Add(_steam); }  // the session already listens, so this listens too
            Status = "Steam: friends can join once you're in a world";
            if (Instance.Auto && Instance.LobbyToJoin != 0) OnJoinRequested(session, Instance.LobbyToJoin);
        }
        else if (Instance.Role == Role.Client && Instance.ConnectAddress == null)
        {
            Status = "Steam: waiting for an invite";
            if (Instance.LobbyToJoin != 0) OnJoinRequested(session, Instance.LobbyToJoin);
        }
        Plugin.Logger.LogInfo($"Steam: up as {SteamClient.Name}; {Status}");
    }

    private static void On<T>(Action<Il2CppSystem.Action<T>> add, Action<T> handler)
    {
        Keep.Add(handler);
        add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(handler));
    }

    private static void On<T1, T2>(Action<Il2CppSystem.Action<T1, T2>> add, Action<T1, T2> handler)
    {
        Keep.Add(handler);
        add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T1, T2>>(handler));
    }

    private static void OnCreated(Result result, Lobby lobby)
    {
        _creating = false;
        if (result != Result.OK) { Status = $"Steam lobby failed ({result})"; Plugin.Logger.LogWarning(Status); return; }
        if (_session is not { Role: Role.Host, Open: true })
        {
            lobby.Leave();  // back at the menu while it was being made
            return;
        }
        _lobby = lobby;
        lobby.SetFriendsOnly();
        lobby.SetJoinable(true);
        lobby.SetData("reka_coop", Protocol.Version.ToString());
        SteamFriends.SetRichPresence("status", "Hosting Reka co-op");
        SteamFriends.SetRichPresence("connect", $"+connect_lobby {lobby.Id.Value}");
        Status = "Steam: friends can join";
        Plugin.Logger.LogInfo($"Steam: lobby {lobby.Id.Value} ready (reka_coop={lobby.GetData("reka_coop")})");
    }

    private static void OnMemberJoined(Friend friend)
    {
        Plugin.Logger.LogInfo($"Steam: {friend.Name} joined the lobby");
        _steam?.Expect(friend.Id.Value);
    }

    private static void OnJoinRequested(CoopSession session, ulong lobbyId)
    {
        if (Instance.Auto && Instance.Role == Role.Host)
        {
            // Joining means leaving our own world: only from the main menu, where nothing of ours is open.
            if (session.IsConnected || !Sync.MenuWatch.Up)
            {
                Sync.DialogueSync.ShowLocal("To join a friend, go back to the main menu first, then join them from Steam again.");
                return;
            }
            CoopDriver.BecomeGuest();
        }
        else if (Instance.Role != Role.Client || Instance.ConnectAddress != null)
        {
            Sync.DialogueSync.ShowLocal("To join a friend, quit Reka and accept the invite again.");
            return;
        }
        if (session.IsConnected)
        {
            Sync.DialogueSync.ShowLocal("You're already playing with a host. Quit to the main menu first, then accept the invite.");
            return;
        }
        Status = "Steam: joining lobby";
        Plugin.Logger.LogInfo($"Steam: joining lobby {lobbyId}");
        _joiningSince = Time.unscaledTime;
        SteamMatchmaking.JoinLobbyAsync(lobbyId);  // OnLobbyEntered carries the result
    }

    private static void OnEntered(CoopSession session, Lobby lobby)
    {
        var owner = lobby.Owner.Id.Value;
        if (owner == SteamClient.SteamId.Value) return;  // our own lobby
        string version = lobby.GetData("reka_coop");
        if (version != Protocol.Version.ToString())
        {
            Status = $"Steam: host runs co-op protocol {version}, this is {Protocol.Version}";
            Sync.DialogueSync.ShowLocal("The host has a different version of the co-op mod. Both need the same one: run \"Update Co-op\" in the game folder.");
            lobby.Leave();
            if (Instance.Auto) CoopDriver.ResumeHosting();
            return;
        }
        _lobby = lobby;
        _joiningSince = -1;
        _connectingSince = Time.unscaledTime;
        Status = $"Steam: host {lobby.Owner.Name}";
        CoopDriver.Waiting = $"Joining {lobby.Owner.Name}'s world...";
        Plugin.Logger.LogInfo($"Steam: in lobby {lobby.Id.Value}; connecting to host {owner}");
        session.Connect(owner.ToString(), 0);
    }

    public static void OpenInvite()
    {
        if (_lobby is { } l) SteamFriends.OpenGameInviteOverlay(l.Id);
    }

    public static void Leave()
    {
        try
        {
            _lobby?.Leave();
            _lobby = null;
            if (_started) SteamFriends.ClearRichPresence();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Steam: leaving: {e.Message}"); }
    }
}
