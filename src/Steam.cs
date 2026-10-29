using System;
using System.Runtime.InteropServices;

namespace VoxelCoop;

// Steamworks flat API, called directly on the steam_api64.dll the game already loaded and initialised.
// Call results are polled through ISteamUtils so no callback registration is needed.
internal static unsafe class Steam
{
    const string Lib = "steam_api64";

    [StructLayout(LayoutKind.Sequential, Size = 136)]
    internal struct Identity
    {
        public int Type;   // k_ESteamNetworkingIdentityType_SteamID = 16
        public int CbSize; // 8
        public ulong SteamId;
        public static Identity Of(ulong id) => new Identity { Type = 16, CbSize = 8, SteamId = id };
    }

    [DllImport(Lib)] static extern bool SteamAPI_IsSteamRunning();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamFriends_v018();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();

    [DllImport(Lib)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);

    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int maxMembers);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);
    [DllImport(Lib)] static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int cmp);
    [DllImport(Lib)] static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(IntPtr self, int filter);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(IntPtr self, int index);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Lib)] static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* buf, int cb, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);

    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, ref Identity id, void* data, uint cb, int flags, int channel);
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* msgs, int max);
    [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg);

    static IntPtr mm, utils, msgs;
    public static bool Ready { get; private set; }
    public static ulong Me { get; private set; }

    public static bool TryInit(out string reason)
    {
        reason = null;
        if (Ready) return true;
        try
        {
            if (!SteamAPI_IsSteamRunning()) { reason = "Steam не запущен"; return false; }
            mm = SteamAPI_SteamMatchmaking_v009();
            utils = SteamAPI_SteamUtils_v010();
            msgs = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
            var user = SteamAPI_SteamUser_v023();
            if (mm == IntPtr.Zero || utils == IntPtr.Zero || msgs == IntPtr.Zero || user == IntPtr.Zero) { reason = "Steam API не инициализирован игрой"; return false; }
            Me = SteamAPI_ISteamUser_GetSteamID(user);
            Ready = Me != 0;
            if (!Ready) reason = "нет SteamID";
            return Ready;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    public static string Name(ulong id)
    {
        var p = SteamAPI_ISteamFriends_GetFriendPersonaName(SteamAPI_SteamFriends_v018(), id);
        return p == IntPtr.Zero ? id.ToString() : Marshal.PtrToStringUTF8(p);
    }

    // --- lobby ---
    public const int LobbyCreatedId = 513, LobbyMatchListId = 510, LobbyEnterId = 504;
    public static ulong CreateLobby(int maxMembers) => SteamAPI_ISteamMatchmaking_CreateLobby(mm, 2 /*public*/, maxMembers);
    public static ulong JoinLobby(ulong lobby) => SteamAPI_ISteamMatchmaking_JoinLobby(mm, lobby);
    public static void LeaveLobby(ulong lobby) => SteamAPI_ISteamMatchmaking_LeaveLobby(mm, lobby);
    public static bool SetLobbyData(ulong lobby, string k, string v) => SteamAPI_ISteamMatchmaking_SetLobbyData(mm, lobby, k, v);
    public static string GetLobbyData(ulong lobby, string k) => Marshal.PtrToStringUTF8(SteamAPI_ISteamMatchmaking_GetLobbyData(mm, lobby, k));
    public static int MemberCount(ulong lobby) => SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(mm, lobby);
    public static ulong Member(ulong lobby, int i) => SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(mm, lobby, i);
    public static ulong Owner(ulong lobby) => SteamAPI_ISteamMatchmaking_GetLobbyOwner(mm, lobby);
    public static ulong FindLobbies(string key, string value)
    {
        SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(mm, key, value, 0 /*equal*/);
        SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(mm, 3 /*worldwide*/);
        return SteamAPI_ISteamMatchmaking_RequestLobbyList(mm);
    }
    public static ulong LobbyByIndex(int i) => SteamAPI_ISteamMatchmaking_GetLobbyByIndex(mm, i);

    /// <summary>0 = pending, 1 = done (result copied), -1 = failed.</summary>
    public static int PollCall<T>(ulong call, int callbackId, out T result) where T : unmanaged
    {
        result = default;
        if (!SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out var failed)) return failed ? -1 : 0;
        if (failed) return -1;
        fixed (T* p = &result)
            return SteamAPI_ISteamUtils_GetAPICallResult(utils, call, p, sizeof(T), callbackId, out failed) && !failed ? 1 : -1;
    }

    [StructLayout(LayoutKind.Sequential)] public struct LobbyCreated { public int Result; public ulong Lobby; }
    [StructLayout(LayoutKind.Sequential)] public struct LobbyMatchList { public uint Count; }
    [StructLayout(LayoutKind.Sequential)] public struct LobbyEnter { public ulong Lobby; public uint Perms; public byte Locked; public uint Response; }

    // --- P2P messages (relayed by Steam, NAT-free) ---
    public const int Channel = 7;
    public static bool Send(ulong to, byte[] data, int len)
    {
        var id = Identity.Of(to);
        fixed (byte* p = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(msgs, ref id, p, (uint)len, 1 /*unreliable, no nagle*/ | 32 /*auto restart broken session*/, Channel) == 1;
    }

    static readonly IntPtr[] recvBuf = new IntPtr[64];
    public delegate void OnMessage(ulong from, byte* data, int len);
    public static int Receive(OnMessage handler)
    {
        int total = 0, n;
        do
        {
            fixed (IntPtr* p = recvBuf) n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(msgs, Channel, p, recvBuf.Length);
            for (int i = 0; i < n; i++)
            {
                var m = (byte*)recvBuf[i];
                // SteamNetworkingMessage_t: m_pData @0, m_cbSize @8, m_identityPeer @16 (steamID64 @ +8)
                handler(*(ulong*)(m + 24), *(byte**)m, *(int*)(m + 8));
                SteamAPI_SteamNetworkingMessage_t_Release(recvBuf[i]);
            }
            total += n;
        } while (n == recvBuf.Length);
        return total;
    }
}
