using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using UnityEngine;
using VoxelPlayground.Gaming;
using VoxelPlayground.Networking.FishNetIntegration;
using VoxelPlayground.UI;

namespace VoxelCoop;

[BepInPlugin("com.pc.voxelcoop", "VoxelCoop", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "1.0.0";
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Internet;
    internal static ConfigEntry<string> ExtraHosts;
    internal static bool DebugUdp, Dev, Ru;
    internal static GameNetworkManager Gnm;
    internal static List<FishNetLanSessionCoordinator> Coordinators = new();

    public override void Load()
    {
        L = Log;
        var args = Environment.GetCommandLineArgs();
        Dev = args.Contains("-vcoopdev");
        DebugUdp = args.Contains("-vcoopudp");
        Internet = Config.Bind("Coop", "Internet", true, "Rooms are also reachable over the internet through Steam (code = Steam lobby).");
        ExtraHosts = Config.Bind("Coop", "ExtraHosts", "", "Comma separated host IPs to look for rooms at directly (Radmin VPN / Hamachi / port forwarding), e.g. 26.10.20.30");
        var lang = Application.systemLanguage;
        Ru = !args.Contains("-vcoopen") && (lang == SystemLanguage.Russian || lang == SystemLanguage.Ukrainian || lang == SystemLanguage.Belarusian);
        // Patch one by one: if a game update moves something, the rest of the mod keeps working.
        var harmony = new Harmony("com.pc.voxelcoop");
        Patch(harmony, typeof(FishNetLanSessionCoordinator), nameof(FishNetLanSessionCoordinator.JoinLanByCode), nameof(Patches.JoinLanByCode), true);
        Patch(harmony, typeof(LanUdpDiscoveryService), "BuildBroadcastEndpoints", nameof(Patches.BuildBroadcastEndpoints), false);
        AddComponent<CoopBehaviour>();
        L.LogInfo($"VoxelCoop {Version} loaded pid={Environment.ProcessId} dev={Dev} udpDebug={DebugUdp}");
    }

    static void Patch(Harmony h, Type type, string method, string patch, bool prefix)
    {
        try
        {
            var target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
            var hm = new HarmonyMethod(AccessTools.Method(typeof(Patches), patch));
            if (prefix) h.Patch(target, prefix: hm); else h.Patch(target, postfix: hm);
        }
        catch (Exception e) { L.LogError($"Could not patch {type.Name}.{method} (game update?): {e.Message}"); }
    }
}

static class Patches
{
    // The joining side asks our tunnel to look the code up on Steam while the game runs its normal LAN search.
    internal static void JoinLanByCode(string code)
    {
        try { Tunnel.BeginJoin(code); } catch (Exception e) { Plugin.L.LogError(e); }
    }

    // Discovery requests also go to the tunnel's local socket and to user-configured direct hosts.
    internal static void BuildBroadcastEndpoints(int discoveryPort, ref Il2CppSystem.Collections.Generic.List<Il2CppSystem.Net.IPEndPoint> __result)
    {
        try
        {
            if (__result == null) __result = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Net.IPEndPoint>();
            if (Plugin.DebugUdp) __result.Clear();
            int port = Tunnel.ClientDiscoveryPort;
            if (port != 0) __result.Add(new Il2CppSystem.Net.IPEndPoint(Il2CppSystem.Net.IPAddress.Loopback, port));
            foreach (var h in Plugin.ExtraHosts.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Il2CppSystem.Net.IPAddress.TryParse(h, out var ip)) __result.Add(new Il2CppSystem.Net.IPEndPoint(ip, discoveryPort));
        }
        catch (Exception e) { Plugin.L.LogError(e); }
    }
}

public class CoopBehaviour : MonoBehaviour
{
    static readonly string CmdFile = Path.Combine(Paths.BepInExRootPath, $"vcoop_cmd_{Environment.ProcessId}.txt");
    float nextPoll, nextScan;
    GUIStyle style;

    void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 1f;
            Scan();
        }
        try { Tunnel.Update(); } catch (Exception e) { Plugin.L.LogError(e); }
        if (!Plugin.Dev || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + 0.25f;
        if (!File.Exists(CmdFile)) return;
        string[] lines;
        try { lines = File.ReadAllLines(CmdFile); File.Delete(CmdFile); } catch { return; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try { Run(line); } catch (Exception e) { Plugin.L.LogError($"cmd '{line}': {e}"); }
        }
    }

    void OnApplicationQuit() => Tunnel.Shutdown();

    // Keeps references fresh and un-hides the game's own (disabled) multiplayer buttons in the title menu.
    void Scan()
    {
        Guard("menu", () =>
        {
            foreach (var m in All<TitleMenuUI>())
                if (m.gameObject.activeInHierarchy && m.btnCreateHost != null && !m.btnCreateHost.gameObject.activeInHierarchy)
                    ShowButtons(m);
        });
        Guard("refs", () =>
        {
            Plugin.Gnm = All<GameNetworkManager>().FirstOrDefault(g => g.gameObject.activeInHierarchy);
            Plugin.Coordinators = All<FishNetLanSessionCoordinator>().Where(c => c.gameObject.activeInHierarchy).ToList();
        });
        // A Steam lobby lookup + relay handshake can eat a few seconds of the game's 8 s LAN search window.
        Guard("timeouts", () =>
        {
            foreach (var c in Plugin.Coordinators)
            {
                if (Plugin.Internet.Value && c.discoveryTimeoutSeconds < 20f) c.discoveryTimeoutSeconds = 20f;
                if (Plugin.Internet.Value && c.fishNetJoinTimeoutSeconds < 20f) c.fishNetJoinTimeoutSeconds = 20f;
            }
        });
    }

    readonly Dictionary<string, int> failures = new();
    void Guard(string what, Action a)
    {
        failures.TryGetValue(what, out int n);
        if (n >= 10) return;
        try { a(); }
        catch (Exception e)
        {
            failures[what] = ++n;
            Plugin.L.LogError(n < 10 ? $"{what}: {e.Message}" : $"{what} disabled after repeated errors (game update?): {e}");
        }
    }

    void OnGUI()
    {
        var s = Tunnel.Status;
        if (string.IsNullOrEmpty(s) || GameplaySessionGateway.CurrentState == GameplaySessionState.InGame) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 16 };
        GUI.color = Color.black;
        GUI.Label(new Rect(11, 9, 900, 30), "VoxelCoop: " + s, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(10, 8, 900, 30), "VoxelCoop: " + s, style);
    }

    void Run(string line)
    {
        var parts = line.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        Plugin.L.LogInfo("> " + line);
        switch (parts[0])
        {
            case "dump": Dump(); break;
            case "show": foreach (var m in All<TitleMenuUI>()) ShowButtons(m); Dump(); break;
            case "host": Plugin.L.LogInfo("CreateLanHost -> " + GameplaySessionGateway.CreateLanHost() + " err=" + GameplaySessionGateway.LastError); break;
            case "join": Plugin.L.LogInfo("JoinLanByCode -> " + GameplaySessionGateway.JoinLanByCode(arg) + " err=" + GameplaySessionGateway.LastError); break;
            case "shot": ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, arg.Length > 0 ? arg : $"vcoop_shot_{Environment.ProcessId}.png")); break;
            case "click": Click(arg); break;
            case "tree": Tree(arg); break;
            case "tp": Teleport(arg); break;
            case "stats": Stats(); Plugin.L.LogInfo("tunnel: " + Tunnel.Status + " | " + Tunnel.Counters); break;
            case "buttons":
                foreach (var b in All<UnityEngine.UI.Button>().Where(b => b.gameObject.activeInHierarchy))
                    Plugin.L.LogInfo("  btn " + PathOf(b.transform));
                break;
            default: Plugin.L.LogWarning("unknown cmd"); break;
        }
    }

    static T[] All<T>() where T : UnityEngine.Object =>
        Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()).Select(o => o.Cast<T>()).ToArray();

    static string HierarchyKey(Transform t) => t.parent == null ? "" : HierarchyKey(t.parent) + t.GetSiblingIndex().ToString("D4");

    static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

    void Dump()
    {
        var log = Plugin.L;
        log.LogInfo($"Gateway: transport={(GameplaySessionGateway.SessionTransport != null)} state={GameplaySessionGateway.CurrentState} code={GameplaySessionGateway.CurrentHostCode} err={GameplaySessionGateway.LastError} players={GameplaySessionGateway.PlayerCount}/{GameplaySessionGateway.Capacity}");
        foreach (var m in All<TitleMenuUI>())
        {
            log.LogInfo($"TitleMenuUI {PathOf(m.transform)} active={m.gameObject.activeInHierarchy}");
            void B(string n, UnityEngine.UI.Button b) => log.LogInfo($"  {n}: {(b == null ? "null" : PathOf(b.transform) + " self=" + b.gameObject.activeSelf + " hier=" + b.gameObject.activeInHierarchy + " interact=" + b.interactable)}");
            B("btnCreateHost", m.btnCreateHost);
            B("btnJoinByCode", m.btnJoinByCode);
            B("btnSandboxMode", m.btnSandboxMode);
            log.LogInfo("  panel: " + (m.multiplayerSessionPanel == null ? "null" : PathOf(m.multiplayerSessionPanel.transform) + " self=" + m.multiplayerSessionPanel.gameObject.activeSelf));
        }
        foreach (var g in All<GameNetworkManager>())
            log.LogInfo($"GameNetworkManager {PathOf(g.transform)} active={g.gameObject.activeInHierarchy} enabled={g.enabled} status={g.RuntimeStatus} diag={g.LastDiagnostic}");
        foreach (var c in All<FishNetLanSessionCoordinator>())
            log.LogInfo($"LanCoordinator {PathOf(c.transform)} active={c.gameObject.activeInHierarchy} enabled={c.enabled}");
        foreach (var n in All<FishNet.Managing.NetworkManager>())
            log.LogInfo($"FishNet NetworkManager {PathOf(n.transform)} active={n.gameObject.activeInHierarchy}");
        foreach (var p in All<MultiplayerSessionPanelUI>())
            log.LogInfo($"MultiplayerSessionPanelUI {PathOf(p.transform)} self={p.gameObject.activeSelf}");
    }

    // Dev only, for framing screenshots: "tp x y z yaw" moves the local player's character.
    void Teleport(string arg)
    {
        var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var p in All<PlayerNetworkAdapter>())
        {
            if (!p.gameObject.activeInHierarchy || !p.IsOwner || p.BoundCharacter == null) continue;
            var t = p.BoundCharacter.transform;
            t.position = new Vector3(v[0], v[1], v[2]);
            if (v.Length > 3) t.rotation = Quaternion.Euler(0, v[3], 0);
            Plugin.L.LogInfo($"tp {t.position} yaw {t.eulerAngles.y}");
        }
    }

    void Stats()
    {
        var log = Plugin.L;
        foreach (var g in All<GameNetworkManager>())
            log.LogInfo($"GNM npcTransport={g.enableNpcCommitTransport} npc tx={g.BroadcastNpcReplicaCommitCount} rx={g.ReceivedNpcReplicaCommitCount} ap={g.AppliedNpcReplicaCommitCount} port={g.defaultPort} cap={g.capacity} overlay={g.showMultiplayerStatusOverlay} vq={g.QueuedVoxelMutationEventCount}");
        foreach (var c in All<FishNetLanSessionCoordinator>())
            log.LogInfo($"Coord fishNetPort={c.fishNetPort} discoveryPort={c.discoveryPort} discTimeout={c.discoveryTimeoutSeconds} joinTimeout={c.fishNetJoinTimeoutSeconds} reconnect={c.reconnectTimeoutSeconds}");
        foreach (var p in All<PlayerNetworkAdapter>())
        {
            if (!p.gameObject.activeInHierarchy) continue;
            var ch = p.BoundCharacter;
            log.LogInfo($"Player id={p.DiagnosticPlayerId.Value} owner={p.IsOwner} char={(ch == null ? "-" : ch.transform.position.ToString())} poseSent={p.SentPoseCount} poseObs={p.ObservedPoseCount} srvAcc={p.ServerAcceptedPoseCount} srvRej={p.ServerRejectedPoseCount} rej='{p.LastPoseRejectReason}' hand tx={p.SentHandOperationRequestCount}/pub={p.PublishedHandOperationCommitCount}/ap={p.AppliedHandOperationCommitCount} fire tx={p.SentWeaponFireCount}/ap={p.AppliedWeaponFireCount} handTr={p.enableHandOperationTransport} fireTr={p.enableWeaponFireTransport} poseSend={p.enableOwnerPoseSend} poseApply={p.enableRemotePoseApply}");
        }
        var cam = Camera.main;
        if (cam != null) log.LogInfo($"camera {cam.transform.position} fwd {cam.transform.forward}");
    }

    static void ShowButtons(TitleMenuUI m)
    {
        for (var t = m.btnCreateHost != null ? m.btnCreateHost.transform : null; t != null && t != m.transform; t = t.parent)
            t.gameObject.SetActive(true);
        if (m.btnJoinByCode != null) m.btnJoinByCode.gameObject.SetActive(true);
    }

    void Click(string path)
    {
        // "name#n" picks the n-th active button with that name (map tiles are all "ItemIconButton(Clone)").
        int nth = 0, hash = path.LastIndexOf('#');
        if (hash > 0 && int.TryParse(path[(hash + 1)..], out nth)) path = path[..hash];
        var go = nth == 0 ? GameObject.Find(path) : null;
        if (go == null)
            go = All<UnityEngine.UI.Button>().Where(x => x.gameObject.activeInHierarchy && (x.name == path || PathOf(x.transform).EndsWith("/" + path)))
                .OrderBy(x => HierarchyKey(x.transform)).Skip(nth).FirstOrDefault()?.gameObject;
        if (go == null) { Plugin.L.LogWarning("not found " + path); return; }
        var b = go.GetComponent<UnityEngine.UI.Button>();
        if (b == null) { Plugin.L.LogWarning("no Button on " + path); return; }
        b.onClick.Invoke();
    }

    void Tree(string path)
    {
        var go = GameObject.Find(path);
        if (go == null) { Plugin.L.LogWarning("not found " + path); return; }
        void Walk(Transform t, int d)
        {
            Plugin.L.LogInfo(new string(' ', d * 2) + t.name + (t.gameObject.activeSelf ? "" : " [off]"));
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), d + 1);
        }
        Walk(go.transform, 0);
    }
}
