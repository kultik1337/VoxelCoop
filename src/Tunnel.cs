using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using VoxelPlayground.Gaming;
using VoxelPlayground.Networking.FishNetIntegration;

namespace VoxelCoop;

// Internet play without port forwarding: the game's own LAN protocol (UDP discovery + FishNet/Tugboat UDP)
// is carried over Steam P2P messages. The joining game believes the host lives on 127.0.0.1.
//
//   client game --UDP--> client tunnel --Steam--> host tunnel --UDP--> host game (127.0.0.1:7771 / :7770)
//
// The 5-letter room code is published as Steam lobby data, so a friend just types the code.
internal static class Tunnel
{
    const byte TagDiscovery = 1, TagGame = 2, TagHello = 3;
    const string LobbyKey = "vcoop_code", LobbyVerKey = "vcoop_ver", ProtoVer = "1";
    const int DebugHostPort = 27760;

    interface ICarrier
    {
        bool Send(ulong peer, byte[] buf, int len);
        void Receive(Action<ulong, byte[], int> handler);
        void Dispose();
    }

    sealed class SteamCarrier : ICarrier
    {
        byte[] tmp = new byte[2048];
        public bool Send(ulong peer, byte[] buf, int len) => Steam.Send(peer, buf, len);
        public unsafe void Receive(Action<ulong, byte[], int> handler) => Steam.Receive((from, data, len) =>
        {
            if (len > tmp.Length) tmp = new byte[len];
            new ReadOnlySpan<byte>(data, len).CopyTo(tmp);
            handler(from, tmp, len);
        });
        public void Dispose() { }
    }

    // Dev-only stand-in for Steam so two instances on one PC (one Steam account) can test the tunnel. Peer id = UDP port.
    sealed class UdpCarrier : ICarrier
    {
        readonly Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        readonly byte[] buf = new byte[2048];
        public UdpCarrier(int port) { s.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
        public bool Send(ulong peer, byte[] b, int len) { s.SendTo(b, 0, len, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, (int)peer)); return true; }
        public void Receive(Action<ulong, byte[], int> handler)
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (s.Available > 0)
            {
                int n;
                try { n = s.ReceiveFrom(buf, ref ep); } catch (SocketException) { continue; }
                handler((ulong)((IPEndPoint)ep).Port, buf, n);
            }
        }
        public void Dispose() => s.Dispose();
    }

    static Socket NewLocalSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // Windows: stop ICMP "port unreachable" from killing later ReceiveFrom calls.
        try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch { }
        return s;
    }

    static readonly object Gate = new();
    static Thread pump;
    static volatile bool pumpRun;
    static ICarrier carrier;
    static readonly byte[] sendBuf = new byte[2048];
    static readonly byte[] recvBuf = new byte[2048];

    public static string Status = "";
    public static long TxGame, RxGame, TxDisc, RxDisc;
    public static string Counters => $"disc tx={TxDisc} rx={RxDisc} game tx={TxGame} rx={RxGame} peers={peerSockets.Count}";
    public static bool Debug => Plugin.DebugUdp;
    static bool connecting;
    static string T(string ru, string en) => Plugin.Ru ? ru : en;

    // ---------------- host side ----------------
    static bool hosting;
    static string hostCode;
    static ulong lobby, lobbyCall;
    static float lobbyRetryAt, nextHello;
    static int discPort = LanDiscoveryProtocol.DefaultDiscoveryPort, gamePort = 7770;
    static readonly Dictionary<ulong, Socket> peerSockets = new();
    static readonly HashSet<ulong> heard = new();

    // ---------------- client side ----------------
    static bool joining;
    static string joinCode;
    static float joinStartedAt, nextJoinStep;
    static ulong joinLobby, joinCall, remoteHost;
    static int joinStage; // 0 search, 1 searching, 2 joining lobby, 3 in lobby, 4 debug delay
    static Socket discSock, gameSock;
    static EndPoint discRequester, gameClient;
    static volatile bool hostHeard;
    public static int ClientDiscoveryPort => joining && discSock != null ? ((IPEndPoint)discSock.LocalEndPoint).Port : 0;

    // Called from the Harmony prefix on FishNetLanSessionCoordinator.JoinLanByCode.
    public static void BeginJoin(string code)
    {
        if (!Plugin.Internet.Value) return;
        code = (code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        if (code.Length == 0) return;
        lock (Gate)
        {
            StopClientLocked();
            if (!Debug && !Steam.TryInit(out var why)) { Status = T("Вход по интернету недоступен: ", "Internet join unavailable: ") + why; Plugin.L.LogWarning(Status); return; }
            joining = true; joinCode = code; joinStage = 0; hostHeard = false; remoteHost = 0;
            joinStartedAt = Time.unscaledTime; nextJoinStep = 0;
            discSock = NewLocalSocket(); gameSock = NewLocalSocket();
            discRequester = gameClient = null;
            carrier = Debug ? new UdpCarrier(0) : new SteamCarrier();
            // Debug: pretend the Steam lookup takes a while, like it does for real.
            if (Debug) { joinStage = 4; nextJoinStep = Time.unscaledTime + 4f; Status = "Connecting (debug UDP)..."; }
            if (!Debug) Status = T($"Ищу комнату {code} через Steam...", $"Looking up room {code} on Steam...");
            Plugin.L.LogInfo($"[tunnel] join {code}, local discovery port {ClientDiscoveryPort}");
            EnsurePump();
        }
    }

    public static void Update()
    {
        var st = GameplaySessionGateway.CurrentState;
        var code = GameplaySessionGateway.CurrentHostCode;
        var gnm = Plugin.Gnm;
        bool server = gnm != null && gnm.FishNetNetworkManager != null && gnm.FishNetNetworkManager.IsServerStarted;
        bool wantHost = Plugin.Internet.Value && server && !string.IsNullOrEmpty(code) && (st == GameplaySessionState.HostingLobby || st == GameplaySessionState.InGame);

        lock (Gate)
        {
            if (wantHost && (!hosting || hostCode != code)) StartHostLocked(code);
            else if (!wantHost && hosting) StopHostLocked();
            if (hosting) UpdateHostLocked();

            if (joining)
            {
                bool dead = st == GameplaySessionState.Solo || st == GameplaySessionState.Ended || st == GameplaySessionState.Failed;
                if (dead && Time.unscaledTime - joinStartedAt > 3f) { Plugin.L.LogInfo($"[tunnel] client session over ({st})"); StopClientLocked(); }
                else UpdateClientLocked();
            }
        }
    }

    static void StartHostLocked(string code)
    {
        StopHostLocked();
        StopClientLocked();
        foreach (var c in Plugin.Coordinators) { discPort = c.discoveryPort; gamePort = c.fishNetPort; }
        if (!Debug && !Steam.TryInit(out var why)) { Status = T("Комната только для локальной сети: ", "Room is LAN-only: ") + why; hosting = true; hostCode = code; return; }
        hosting = true; hostCode = code; lobby = 0; lobbyCall = 0; lobbyRetryAt = 0;
        carrier = Debug ? new UdpCarrier(DebugHostPort) : new SteamCarrier();
        Status = Debug ? "Tunnel open (debug UDP)" : T("Создаю Steam-лобби...", "Creating Steam lobby...");
        Plugin.L.LogInfo($"[tunnel] host {code}, game {gamePort}, discovery {discPort}");
        EnsurePump();
    }

    static void UpdateHostLocked()
    {
        if (carrier == null) return;
        if (!Debug)
        {
            if (lobby == 0 && lobbyCall == 0 && Time.unscaledTime >= lobbyRetryAt)
                lobbyCall = Steam.CreateLobby(Math.Max(2, GameplaySessionGateway.Capacity));
            if (lobbyCall != 0)
            {
                int r = Steam.PollCall<Steam.LobbyCreated>(lobbyCall, Steam.LobbyCreatedId, out var res);
                if (r != 0)
                {
                    lobbyCall = 0;
                    if (r > 0 && res.Result == 1)
                    {
                        lobby = res.Lobby;
                        Steam.SetLobbyData(lobby, LobbyKey, hostCode);
                        Steam.SetLobbyData(lobby, LobbyVerKey, ProtoVer);
                        Status = T($"Комната {hostCode} открыта для друзей по интернету (Steam)", $"Room {hostCode} is open to friends over the internet (Steam)");
                        Plugin.L.LogInfo($"[tunnel] lobby {lobby} code {hostCode}");
                    }
                    else { Status = T("Не удалось создать Steam-лобби, пробую ещё раз...", "Could not create Steam lobby, retrying..."); lobbyRetryAt = Time.unscaledTime + 5f; }
                }
            }
            // Sending to a lobby member implicitly accepts their pending P2P session.
            if (lobby != 0 && Time.unscaledTime >= nextHello)
            {
                nextHello = Time.unscaledTime + 0.5f;
                int n = Steam.MemberCount(lobby);
                for (int i = 0; i < n; i++)
                {
                    var m = Steam.Member(lobby, i);
                    if (m != 0 && m != Steam.Me && !heard.Contains(m)) SendTagged(m, TagHello, null, 0);
                }
            }
        }
    }

    static void StopHostLocked()
    {
        if (!hosting) return;
        hosting = false;
        if (lobby != 0) { Steam.LeaveLobby(lobby); Plugin.L.LogInfo($"[tunnel] left lobby {lobby}"); }
        lobby = 0; lobbyCall = 0;
        foreach (var s in peerSockets.Values) s.Dispose();
        peerSockets.Clear(); heard.Clear();
        if (!joining) DropCarrierLocked();
        Status = "";
    }

    static void UpdateClientLocked()
    {
        if (Time.unscaledTime < nextJoinStep) return;
        switch (joinStage)
        {
            case 0:
                joinCall = Steam.FindLobbies(LobbyKey, joinCode);
                joinStage = 1;
                break;
            case 1:
            {
                int r = Steam.PollCall<Steam.LobbyMatchList>(joinCall, Steam.LobbyMatchListId, out var res);
                if (r == 0) break;
                ulong found = 0;
                if (r > 0)
                    for (int i = 0; i < res.Count && found == 0; i++)
                    {
                        var l = Steam.LobbyByIndex(i);
                        if (Steam.GetLobbyData(l, LobbyKey) == joinCode) found = l;
                    }
                if (found == 0)
                {
                    // Not on Steam (yet) - maybe it's a LAN room. Keep looking while the game's own search runs.
                    Status = T($"Комната {joinCode} не найдена в Steam, ищу в локальной сети...", $"Room {joinCode} not found on Steam, searching LAN...");
                    joinStage = 0; nextJoinStep = Time.unscaledTime + 1.5f;
                    break;
                }
                var hostVer = Steam.GetLobbyData(found, LobbyVerKey);
                if (hostVer != ProtoVer)
                {
                    Plugin.L.LogWarning($"[tunnel] host runs VoxelCoop protocol {hostVer}, we run {ProtoVer}");
                    Status = T("У хоста другая версия VoxelCoop, обновитесь оба до последней", "Host has a different VoxelCoop version, both of you should update");
                    joinStage = 0; nextJoinStep = Time.unscaledTime + 5f;
                    break;
                }
                joinLobby = found;
                joinCall = Steam.JoinLobby(found);
                joinStage = 2;
                break;
            }
            case 2:
            {
                int r = Steam.PollCall<Steam.LobbyEnter>(joinCall, Steam.LobbyEnterId, out var res);
                if (r == 0) break;
                if (r < 0 || res.Response != 1) { Status = T("Не удалось зайти в Steam-лобби", "Could not join the Steam lobby"); joinStage = 0; nextJoinStep = Time.unscaledTime + 1.5f; break; }
                remoteHost = Steam.Owner(joinLobby);
                Status = T("Подключаюсь к ", "Connecting to ") + Steam.Name(remoteHost) + "...";
                connecting = true;
                Plugin.L.LogInfo($"[tunnel] in lobby {joinLobby}, host {remoteHost}");
                joinStage = 3;
                break;
            }
            case 4:
                remoteHost = DebugHostPort; joinStage = 3;
                break;
            case 3:
                if (!hostHeard) { SendTagged(remoteHost, TagHello, null, 0); nextJoinStep = Time.unscaledTime + 0.5f; }
                else if (connecting) { connecting = false; Status = T("Соединение через Steam установлено", "Connected through Steam"); }
                break;
        }
    }

    static void StopClientLocked()
    {
        if (!joining) return;
        joining = false;
        if (joinLobby != 0) Steam.LeaveLobby(joinLobby);
        joinLobby = 0; remoteHost = 0; joinStage = 0;
        discSock?.Dispose(); gameSock?.Dispose(); discSock = gameSock = null;
        if (!hosting) DropCarrierLocked();
        Status = "";
    }

    static void DropCarrierLocked()
    {
        carrier?.Dispose();
        carrier = null;
    }

    static void SendTagged(ulong peer, byte tag, byte[] data, int len)
    {
        if (carrier == null) return;
        sendBuf[0] = tag;
        if (len > 0) Buffer.BlockCopy(data, 0, sendBuf, 1, len);
        carrier.Send(peer, sendBuf, len + 1);
    }

    // ---------------- packet pump (background thread) ----------------
    static void EnsurePump()
    {
        if (pump != null) return;
        pumpRun = true;
        pump = new Thread(PumpLoop) { IsBackground = true, Name = "VoxelCoop tunnel" };
        pump.Start();
    }

    public static void Shutdown()
    {
        pumpRun = false;
        lock (Gate) { StopHostLocked(); StopClientLocked(); }
    }

    static readonly Regex PortRx = new("\"fishNetPort\"\\s*:\\s*\\d+");

    static void PumpLoop()
    {
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (pumpRun)
        {
            try
            {
                lock (Gate)
                {
                    carrier?.Receive(OnCarrier);
                    if (hosting)
                        foreach (var kv in peerSockets)
                            while (kv.Value.Available > 0)
                            {
                                int n = kv.Value.ReceiveFrom(recvBuf, ref any);
                                int port = ((IPEndPoint)any).Port;
                                if (port == discPort) TxDisc++; else TxGame++;
                                SendTagged(kv.Key, port == discPort ? TagDiscovery : TagGame, recvBuf, n);
                            }
                    if (joining && discSock != null)
                    {
                        while (discSock.Available > 0)
                        {
                            int n = discSock.ReceiveFrom(recvBuf, ref any);
                            discRequester = any;
                            if (remoteHost != 0) { TxDisc++; SendTagged(remoteHost, TagDiscovery, recvBuf, n); }
                            any = new IPEndPoint(IPAddress.Any, 0);
                        }
                        while (gameSock.Available > 0)
                        {
                            int n = gameSock.ReceiveFrom(recvBuf, ref any);
                            gameClient = any;
                            if (remoteHost != 0) { TxGame++; SendTagged(remoteHost, TagGame, recvBuf, n); }
                            any = new IPEndPoint(IPAddress.Any, 0);
                        }
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Plugin.L.LogError("[tunnel] " + e); Thread.Sleep(100); }
            Thread.Sleep(1);
        }
        pump = null;
    }

    static void OnCarrier(ulong from, byte[] buf, int len)
    {
        if (len < 1) return;
        byte tag = buf[0];
        if (hosting)
        {
            if (!Debug && lobby == 0) return;
            if (heard.Add(from))
            {
                Plugin.L.LogInfo($"[tunnel] peer {from} connected");
                if (!Debug) SendTagged(from, TagHello, null, 0);
            }
            if (tag == TagHello) { if (Debug) SendTagged(from, TagHello, null, 0); return; }
            if (tag == TagDiscovery) RxDisc++; else RxGame++;
            if (!peerSockets.TryGetValue(from, out var s)) peerSockets[from] = s = NewLocalSocket();
            s.SendTo(buf, 1, len - 1, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, tag == TagDiscovery ? discPort : gamePort));
            return;
        }
        if (joining && from == remoteHost)
        {
            hostHeard = true;
            if (tag == TagDiscovery) RxDisc++; else if (tag == TagGame) RxGame++;
            if (tag == TagDiscovery && discRequester != null)
            {
                // Point the joining game at our local game socket instead of the host's real FishNet port.
                var json = Encoding.UTF8.GetString(buf, 1, len - 1);
                var port = ((IPEndPoint)gameSock.LocalEndPoint).Port;
                var fixedJson = PortRx.Replace(json, "\"fishNetPort\":" + port);
                if (fixedJson == json) Plugin.L.LogWarning("[tunnel] no fishNetPort in discovery reply: " + json);
                var bytes = Encoding.UTF8.GetBytes(fixedJson);
                discSock.SendTo(bytes, discRequester);
            }
            else if (tag == TagGame && gameClient != null)
                gameSock.SendTo(buf, 1, len - 1, SocketFlags.None, gameClient);
        }
    }
}
