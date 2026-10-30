using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RekaCoop.Net;

// ISteamNetworkingMessages through the flat API of the game's own steam_api64.dll (no second Steam binding).
// Signatures from the Steamworks SDK's steam_api_flat.h; the exports are checked in research/steam-lobbies.md.
public unsafe class SteamWire : IMessageWire
{
    private const int Channel = 0;
    private const int SendUnreliable = 1, SendReliable = 8 | 32;  // NoNagle; Reliable | AutoRestartBrokenSession

    // Steam calls OnSessionRequest from its own callback pump, so these are static and thread-safe.
    private static readonly ConcurrentDictionary<ulong, bool> Allowed = new();
    private static volatile bool _acceptAny;
    // A plain delegate, not [UnmanagedCallersOnly]: UnityEngine.CoreModule's interop defines that attribute too.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SessionRequestFn(Identity* remote);
    private static readonly SessionRequestFn SessionRequest = OnSessionRequest;  // kept alive for Steam
    private IntPtr _api;

    public void Open(bool acceptAnyone)
    {
        _acceptAny = acceptAnyone;
        if (_api != IntPtr.Zero) return;
        _api = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        if (_api == IntPtr.Zero) throw new InvalidOperationException("Steam networking is not available (is Steam running?)");
        var utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        if (utils == IntPtr.Zero) return;
        SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils);
        SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(utils, Marshal.GetFunctionPointerForDelegate(SessionRequest));
    }

    public void Allow(ulong steamId) => Allowed[steamId] = true;

    private static void OnSessionRequest(Identity* remote)
    {
        if (!_acceptAny && !Allowed.ContainsKey(remote->SteamId)) return;
        var id = *remote;
        SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(SteamAPI_SteamNetworkingMessages_SteamAPI_v002(), ref id);
    }

    public void Send(ulong to, byte[] data, int length, bool reliable)
    {
        var id = Identity.Of(to);
        fixed (byte* b = data)
            SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_api, ref id, (IntPtr)b, (uint)length, reliable ? SendReliable : SendUnreliable, Channel);
    }

    public void Receive(WireMessage onMessage)
    {
        var msgs = stackalloc IntPtr[32];
        int n;
        while ((n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_api, Channel, msgs, 32)) > 0)
            for (int i = 0; i < n; i++)
            {
                var m = (Message*)msgs[i];
                try { onMessage(m->Remote.SteamId, new ReadOnlySpan<byte>((void*)m->Data, m->Size)); }
                finally { SteamAPI_SteamNetworkingMessage_t_Release(msgs[i]); }
            }
    }

    public void Close(ulong steamId)
    {
        var id = Identity.Of(steamId);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_api, ref id);
    }

    // SteamNetworkingIdentity: type, size, then a 128-byte union whose first 8 bytes are the Steam ID.
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct Identity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Size;
        [FieldOffset(8)] public ulong SteamId;
        public static Identity Of(ulong steamId) => new() { Type = 16, Size = 8, SteamId = steamId };
    }

    // The start of SteamNetworkingMessage_t.
    [StructLayout(LayoutKind.Explicit)]
    private struct Message
    {
        [FieldOffset(0)] public IntPtr Data;
        [FieldOffset(8)] public int Size;
        [FieldOffset(16)] public Identity Remote;
    }

    private const string Lib = "steam_api64";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(IntPtr self, IntPtr callback);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, ref Identity remote, IntPtr data, uint size, int flags, int channel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref Identity remote);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref Identity remote);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
}
