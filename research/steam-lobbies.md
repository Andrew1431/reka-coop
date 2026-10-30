# Steam lobbies and invites (research, 2026-10-26)

Slice goal: friends join without port forwarding. Read-only research; nothing launched or edited.
Tags: **[V]** verified (seen in interop dumps / DLL bytes / repo), **[I]** inferred (from Facepunch/Steamworks
SDK knowledge or reasoning, needs a runtime check).

## 1. What is available

### Native side
- `Reka_Data/Plugins/x86_64/steam_api64.dll`, 319,584 bytes, FileVersion 09.31.86.04, SHA-256
  670d654a...8e83e2d. **[V]** Interface strings: SteamFriends017, SteamMatchMaking009, SteamNetworking006,
  SteamNetworkingSockets012, SteamNetworkingMessages002, SteamNetworkingUtils004, SteamUser023, SteamClient021. **[V]**
  That is roughly Steamworks SDK 1.58-1.61. **[I]**
- Flat exports present **[V]**: `SteamAPI_SteamNetworkingMessages_SteamAPI_v002`,
  `SteamAPI_ISteamNetworkingMessages_{SendMessageToUser,ReceiveMessagesOnChannel,AcceptSessionWithUser,CloseSessionWithUser,CloseChannelWithUser,GetSessionConnectionInfo}`,
  `SteamAPI_SteamNetworkingMessage_t_Release`, `SteamAPI_SteamNetworkingUtils_SteamAPI_v004`,
  `SteamAPI_ISteamNetworkingUtils_{InitRelayNetworkAccess,SetGlobalCallback_MessagesSessionRequest,SetGlobalCallback_MessagesSessionFailed,SetGlobalConfigValuePtr}`,
  `SteamAPI_ISteamNetworkingSockets_{CreateListenSocketP2P,ConnectP2P,ReceiveMessagesOnConnection}`,
  `SteamAPI_ISteamNetworking_SendP2PPacket`, `SteamAPI_ISteamMatchmaking_CreateLobby`,
  `SteamAPI_ISteamFriends_{ActivateGameOverlayInviteDialog,SetRichPresence}`, `SteamAPI_ManualDispatch_*`.

### Facepunch.Steamworks.Win64.dll interop (all signatures **[V]** from dumper)
Facepunch version ~2.4.x (has `SteamTimeline`). **[I]** Interop listing means the method survived IL2CPP linking,
so a native body exists **[I]**; open generic methods (`CreateRelaySocket<T>`, `ConnectRelay<T>`) only work for
instantiations the game compiled, i.e. effectively unusable. **[I]**

SteamMatchmaking (static):
- `Task<Lobby?> CreateLobbyAsync(int maxMembers)`, `Task<Lobby?> JoinLobbyAsync(SteamId lobbyId)` (returns `Il2CppSystem` Task)
- events (`Il2CppSystem.Action`, add_/remove_ present): `OnLobbyCreated(Result, Lobby)`, `OnLobbyEntered(Lobby)`,
  `OnLobbyInvite(Friend, Lobby)`, `OnLobbyMemberJoined/Leave/Disconnected(Lobby, Friend)`, `OnLobbyDataChanged(Lobby)`,
  `OnLobbyMemberDataChanged`, `OnLobbyGameCreated(Lobby, uint, ushort, SteamId)`, `OnChatMessage(Lobby, Friend, string)`
- `Internal` (ISteamMatchmaking): `CreateLobby(LobbyType, int)`, `JoinLobby(SteamId)`, `SetLobbyData`, `GetLobbyOwner`, ... all present.

Steamworks.Data.Lobby (blittable struct, field `SteamId Id`): `Lobby(SteamId)`, `Task<RoomEnter> Join()`, `Leave()`,
`InviteFriend(SteamId)`, `string GetData(string)`, `bool SetData(string,string)`, `SetMemberData`, `GetMemberData`,
`SetPublic/SetPrivate/SetFriendsOnly/SetInvisible()`, `SetJoinable(bool)`, `Friend Owner`, `int MemberCount`,
`IEnumerable<Friend> Members`, `SetGameServer(SteamId)`, `IsOwnedBy(SteamId)`. LobbyType enum: Private 0,
FriendsOnly 1, Public 2, Invisible 3, PrivateUnique 4.

SteamFriends (static): `OpenGameInviteOverlay(SteamId lobby)`, `SetRichPresence(string,string)`, `ClearRichPresence()`,
`GetRichPresence(string)`, `OpenOverlay(string)`, `SetPlayedWith(SteamId)`, events `OnGameLobbyJoinRequested(Lobby, SteamId)`,
`OnGameRichPresenceJoinRequested(Friend, string)`, `OnGameOverlayActivated(bool)`.

SteamClient: `SteamId SteamId`, `string Name`, `bool IsValid`, `RunCallbacks()`, `Init(uint, bool asyncCallbacks)`.
SteamId: `ulong Value`, implicit ulong conversions.

SteamNetworkingSockets: `SocketManager CreateRelaySocket(int virtualport, ISocketManager)`,
`ConnectionManager ConnectRelay(SteamId, int virtualport, IConnectionManager)`, normal/FakeIP variants, event
`OnConnectionStatusChanged(Connection, ConnectionInfo)`. `ISocketManager`/`IConnectionManager` are
`Il2CppObjectBase` interface wrappers (OnConnecting/OnConnected/OnDisconnected/OnMessage) -> using them needs
`ClassInjector.RegisterTypeInIl2Cpp` with interface implementation. `Connection.SendMessage(IntPtr, int, SendType, ushort lane)`.

SteamNetworking (old P2P, deprecated by Valve): `SendP2PPacket(SteamId, byte*, uint, int channel, P2PSend)`,
`ReadP2PPacket(byte*, uint, out uint, out SteamId, int)`, `IsP2PPacketAvailable(out uint, int)`,
`AcceptP2PSessionWithUser`, event `OnP2PSessionRequest(SteamId)`. P2PSend: Unreliable, UnreliableNoDelay, Reliable,
ReliableWithBuffering.

ISteamNetworkingMessages: only the low-level `Internal`-style class exists (`SendMessageToUser(ref NetIdentity,
Il2CppStructArray<byte>, uint, int flags, int channel)`, `ReceiveMessagesOnChannel(int, IntPtr, int)`, accept/close).
**No** high-level `SteamNetworkingMessages` wrapper and no session-request event in Facepunch. **[V]**
`Data.NetMsg` layout mirrors `SteamNetworkingMessage_t`: DataPtr, DataSize, Connection, NetIdentity, ConnUserData,
RecvTime, MessageNumber, FreeDataPtr, ReleasePtr, Channel, Flags, UserData, IdxLane. **[V]**

### Game side (Assembly-CSharp) **[V]**
`SteamWorksManager : MonoBehaviour` (Awake, StartSteamClient, InitSteamClient(uint), RefuseToRun, Update, OnDisable,
achievements; `static s_current`). `SteamLaunchGuard` (MayStartBeforeInit/MayRunAfterInit). AppIds enum includes
Reka 1737870, Reka_TestApp 3892170. The game uses Facepunch's dispatcher (`Steamworks.Dispatch`, ManualDispatch);
whether `Update` pumps `SteamClient.RunCallbacks` or Init used asyncCallbacks is unknown. **[I]** We never touch the guard.

### Big Walk carry-over
Big Walk networking is Mirror over EOS lobbies, not Steam transport; only the patterns carry over: raw handler
ids, Hello/HelloAck protocol gate, host relay, replay state to late joiners, second-PC test rig ("Olga" over SMB). **[V]**

## 2. Recommendation

Hybrid:
1. **Lobbies, invites, rich presence: game's Facepunch interop.** These are callback-driven, and the game already
   owns the Steam callback pump (Facepunch ManualDispatch). If we registered our own callbacks via raw steam_api,
   Facepunch's dispatcher would pull and drop them. **[I]** Facepunch has already installed the matchmaking/friends
   handlers in `InitializeInterface`, so we just add delegates to its static events. **[I]**
2. **Data path: direct P/Invoke to ISteamNetworkingMessages flat API** from the plugin (net6, `unsafe`).
   - Connectionless, SteamID-addressed, relayed over Valve SDR (no port forwarding, NAT-proof). Sessions per peer.
   - No Il2Cpp allocation per packet (Facepunch's wrapper wants `Il2CppStructArray<byte>`), plain `byte*`.
   - No interface injection (unlike SocketManager/ConnectionManager).
   - Session-request callback bypasses the dispatcher: `SetGlobalCallback_MessagesSessionRequest(fnptr)` takes a
     plain C function pointer (`UnmanagedCallersOnly`) **[V export, I behaviour]**; plus the sender-first trick
     (sending to a peer implicitly accepts it). **[I]**
   - Rejected: old `SteamNetworking` P2P (deprecated, still works but no future); `SteamNetworkingSockets` via
     Facepunch (class injection of ISocketManager plus Il2Cpp delegate marshalling; worth it only if Messages fails);
     sockets via flat API (needs connection-status callbacks, more state than Messages for no gain at 2-4 players).
3. **LiteNetLib stays for loopback/LAN dev**, behind an `ITransport`. LiteNetLib 2.1.4 has no pluggable socket
   (only `PacketLayerBase` for encrypt/CRC on the same UDP socket, `UseNativeSockets`, `ManualUpdate/ManualReceive`)
   **[V names in DLL, I semantics]**, so do not try to tunnel it through Steam. Keep `NetDataWriter`/`NetDataReader`
   (LiteNetLib.Utils) as the serializer for both transports; they don't depend on sockets. **[I]**

No NuGet packages needed (no Steamworks.NET; it would load a second steam_api binding into the same process).

## 3. Design

### ITransport (Net/ITransport.cs)
```
enum Delivery { Reliable, Unreliable, Sequenced }
interface ITransport {
  bool Listen();                         // host
  void Connect(string target);           // "127.0.0.1:27877" or "steam:7656119..."
  void Poll();                           // main thread; raises events below
  void Send(int conn, byte[] data, int len, Delivery d);
  void Disconnect(int conn);
  void Stop();
  event Action<int> Connected; event Action<int, string> Disconnected;
  event Action<int, NetDataReader> Received;
}
```
- `UdpTransport`: today's NetManager code, conn = NetPeer.Id, Delivery -> ReliableOrdered/Unreliable/Sequenced.
- `SteamTransport`: conn = small int mapped to SteamId. Send flags: Reliable = 8 (k_nSteamNetworkingSend_Reliable)
  on channel 0 (ordered per channel **[I]**); Unreliable = 0/NoNagle 1 on channel 1; Sequenced = unreliable on
  channel 1 with a `ushort seq` prefix and receiver drops older. Receive: `ReceiveMessagesOnChannel(ch 0 and 1,
  IntPtr* buf, 32)`, read `m_pData/m_cbSize/m_identityPeer.steamid` (offsets 0, 8, 16+8), copy, then
  `SteamAPI_SteamNetworkingMessage_t_Release`. `Connected` fires on first message from an allowed SteamId;
  `Disconnected` from lobby leave/disconnect events, SessionFailed callback, or ping timeout (add a 10 s timeout to
  CoopSession; UDP gets it from LiteNetLib today).
- Allow-list: host only accepts sessions/messages from SteamIds currently in its lobby.
- CoopSession changes: hold `ITransport`; `RemotePlayer.Peer` -> `int Conn` (+ `ulong SteamId`); `Message` event
  passes `NetDataReader` instead of `NetPacketReader` (callers only use `GetX`, so source change is mechanical);
  `DeliveryMethod` -> `Delivery` in PlayerSync.cs:50,79 and AppearanceSync.cs:130-131. Hello adds `ulong steamId`
  (protocol v4) so the host can key profiles per Steam ID (NOTES "sidecar per Steam ID").
- Flat signatures (SDK steam_api_flat.h, **[I]**; exports **[V]**), all `CallingConvention.Cdecl`:
  `IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002()`;
  `int SendMessageToUser(IntPtr self, ref Identity id, void* data, uint cb, int flags, int ch)` (EResult, 1 = OK);
  `int ReceiveMessagesOnChannel(IntPtr self, int ch, IntPtr* msgs, int max)`;
  `bool AcceptSessionWithUser(IntPtr self, ref Identity)`, `CloseSessionWithUser(...)`;
  `void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg)`;
  `bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(IntPtr utils, delegate* unmanaged[Cdecl]<SessionRequest_t*, void>)`;
  `void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr utils)`.
  `Identity` = SteamNetworkingIdentity, 136 bytes: `int type` (16 = SteamID), `int size` (8), `ulong steamId` at +8,
  rest zero. (Facepunch `NetIdentity` has the same fields **[V]**.)

### Lobby flow (Net/SteamLobby.cs, Unity-side, main thread)
Host (`-coopsteam host`, later a menu button):
1. `InitRelayNetworkAccess`; `CreateLobbyAsync(4)` (ignore the Task; use events).
2. `OnLobbyCreated(Ok, lobby)` -> `SetFriendsOnly()`, `SetJoinable(true)`, `SetData("reka_coop", Protocol.Version)`,
   `SetData("host", SteamClient.SteamId)`; `SetRichPresence("connect", "+coop_lobby <lobbyId>")`, `"status"`.
3. Invite via `OpenGameInviteOverlay(lobby.Id)` (or friends list "Join Game").
4. `OnLobbyMemberJoined(lobby, friend)` -> add to allow-list, send a `Welcome` probe (implicit session accept).
   `OnLobbyMemberLeave/Disconnected` -> drop peer, `CloseSessionWithUser`.

Guest:
1. Game running: `OnGameLobbyJoinRequested(lobby, friend)` (invite / Join Game) or
   `OnGameRichPresenceJoinRequested(friend, connect)`. Game not running: Steam launches it with `+connect_lobby <id>`
   (or our `+coop_lobby <id>`) on the command line; parse in `Instance.Init`. **[I]**
2. `JoinLobbyAsync(id)` -> `OnLobbyEntered(lobby)` -> check `GetData("reka_coop") == Protocol.Version`,
   host = `lobby.Owner.Id` (or `GetData("host")`).
3. `SteamTransport.Connect("steam:<host>")` -> CoopSession sends Hello as today -> HelloAck. Leave lobby on disconnect.
Lobby is only a rendezvous; game traffic never goes through lobby chat.

New messages: none on the wire besides `Welcome` (host->guest, transport-level, may be a Ping) and `steamId` in Hello.

### Local dev with two copies, one account
Same SteamId in both copies: Messages-to-self and joining your own lobby do not give a usable second peer. **[I]**
Keep `-coophost/-coopconnect` UDP as the default; Steam transport only with `-coopsteam`. One copy can still test
the host half alone: lobby created, data set, overlay opens, rich presence visible. The guest half needs a second
Steam account that owns Reka on another PC (Family Sharing fails while the owner plays; NOTES.md line 29).
Launching via Steam "Join Game" starts the real install, not game/client: put the dev build there on that PC.

## 4. Risks / open questions (runtime)
1. Does SteamWorksManager pump callbacks on the main thread (`RunCallbacks` in Update) or async? Affects thread safety
   of our Facepunch event handlers (Il2Cpp delegates on a worker thread). Test: log `Thread.CurrentThread.ManagedThreadId`
   in OnLobbyCreated. Queue to main thread regardless.
2. System->Il2Cpp delegate conversion for `Il2CppSystem.Action<Lobby,SteamId>` / `Action<Result,Lobby>` with value-type
   args. Expected to work via Il2CppInterop `DelegateSupport`; verify with OnLobbyCreated first.
3. Are Facepunch method bodies callable (some of the 957 failed unstrips may be here)? A failing call throws at
   first use: smoke-test each API once.
4. Does `CreateLobbyAsync` complete when its Task is never awaited (Facepunch async continuations on IL2CPP)?
   Fallback: `SteamMatchmaking.Internal.CreateLobby(LobbyType.FriendsOnly, 4)` + events.
5. Global `MessagesSessionRequest` callback: fires from inside the game's dispatch, possibly not the main thread.
   Handler must only enqueue. Does the sender-first trick alone suffice?
6. Steam overlay in HDRP/DX12 window: if disabled/not injecting, `OpenGameInviteOverlay` does nothing. Fallback:
   friends list "Join Game" via rich presence `connect`, or invite by `lobby.InviteFriend(steamId)`.
7. Does the game ever call `SteamFriends.SetRichPresence/ClearRichPresence` itself (overwriting ours)?
8. Ordering guarantee of reliable Messages on one channel, and unreliable size (Appearance ~1 KB fits; keep
   unreliable < 1100 bytes).
9. Need a second Steam account + PC (owning Reka) for any guest test. Ask Andrew which machine/account.
10. Guest world state: guest currently must load the same save (NOTES). A Steam join from the main menu needs a
    world-transfer slice; out of scope here but blocks a real "click invite and play".

## 5. First implementation step (small, host-only, testable on this PC with one copy)
Add `Net/SteamLobby.cs` behind `-coopsteam host`: after `SteamClient.IsValid`, subscribe `OnLobbyCreated`,
`OnLobbyEntered`, `OnLobbyMemberJoined`, `OnGameLobbyJoinRequested` (log thread id + args), call
`CreateLobbyAsync(4)`, on created set FriendsOnly + `reka_coop` data + rich presence `connect`, then
`OpenGameInviteOverlay(lobby.Id)` on a hotkey. Also P/Invoke `SteamAPI_SteamNetworkingMessages_SteamAPI_v002()` and
`InitRelayNetworkAccess` and log non-zero pointers. Success = log shows lobby id, data read back via `GetData`,
overlay opens. No transport change yet; ITransport refactor (UDP only, no behaviour change) is step 2,
SteamTransport step 3, two-PC test step 4.
