// PeakGhosts.cs
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Single-file BepInEx plugin implementing:
/// - embedded TCP relay (host/listen + broadcast)
/// - real-time POSE messages (POSE|peerId|x,y,z|rx,ry,rz)
/// - ghost spawn (capsule) with translucent material
/// - hotkey F10 toggles muting of remote AudioSources
/// 
/// Build: target .NET Framework 4.7.2 (or compatible), reference BepInEx.dll and UnityEngine DLLs.
/// </summary>
namespace PeakGhosts
{
    [BepInPlugin("dev.beckerlabs.peak.ghosts", "Peak Ghosts (single DLL)", "1.0.0")]
    public class PeakGhostsPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private ConfigEntry<bool> cfgMuteVoice;
        private ConfigEntry<string> cfgRelayHost;
        private ConfigEntry<int> cfgRelayPort;
        private GhostManager ghostManager;

        void Awake()
        {
            Log = Logger;
            cfgMuteVoice = Config.Bind("General", "MuteOtherVoice", false, "Start with muting other players' voice chat");
            cfgRelayHost = Config.Bind("Network", "RelayHost", "127.0.0.1", "IP address of relay (set to your VPS or host's IP).");
            cfgRelayPort = Config.Bind("Network", "RelayPort", 8765, "Port for relay.");

            // Create a persistent GameObject to host manager
            var go = new GameObject("PeakGhosts_Manager");
            ghostManager = go.AddComponent<GhostManager>();
            ghostManager.Init(cfgRelayHost.Value, cfgRelayPort.Value, cfgMuteVoice.Value);
            DontDestroyOnLoad(go);

            Log.LogInfo("[PeakGhosts] Loaded. Press F10 to toggle voice mute (config key: MuteOtherVoice).");
        }

        void Update()
        {
            // Toggle by F10 and update config + manager
            if (Input.GetKeyDown(KeyCode.F10))
            {
                cfgMuteVoice.Value = !cfgMuteVoice.Value;
                ghostManager.SetMuteVC(cfgMuteVoice.Value);
                Log.LogInfo($"[PeakGhosts] MuteOtherVoice = {cfgMuteVoice.Value}");
            }
        }

        void OnDestroy()
        {
            ghostManager?.Shutdown();
        }
    }

    public class GhostManager : MonoBehaviour
    {
        // Simple ghost structure
        class Ghost
        {
            public string id;
            public GameObject go;
            public Vector3 targetPos;
            public Quaternion targetRot;
            public float lastUpdateTime;
        }

        private readonly Dictionary<string, Ghost> ghosts = new Dictionary<string, Ghost>();
        private Material ghostMaterial;
        private string relayHost = "127.0.0.1";
        private int relayPort = 8765;
        private TcpListener listener;
        private readonly List<TcpClient> connectedClients = new List<TcpClient>();
        private TcpClient outboundClient;
        private NetworkStream outboundStream;
        private CancellationTokenSource shutdownCts = new CancellationTokenSource();
        private string myId;
        private GameObject localPlayer;
        private bool muteVC = false;
        private readonly object clientsLock = new object();
        private readonly object ghostsLock = new object();

        // rate controls
        private float lastSend = 0f;
        private const float sendInterval = 0.1f; // 10 Hz

        public void Init(string host, int port, bool startMuted)
        {
            relayHost = host;
            relayPort = port;
            muteVC = startMuted;
            myId = SystemInfo.deviceUniqueIdentifier;

            ghostMaterial = BuildGhostMaterial();

            // start networking tasks
            Task.Run(() => StartListener(shutdownCts.Token));
            Task.Run(() => StartOutboundConnectionLoop(shutdownCts.Token));
        }

        public void SetMuteVC(bool muted)
        {
            muteVC = muted;
        }

        public void Shutdown()
        {
            try
            {
                shutdownCts?.Cancel();

                lock (clientsLock)
                {
                    foreach (var c in connectedClients)
                    {
                        try { c?.Close(); } catch { }
                    }
                    connectedClients.Clear();
                }

                try { outboundStream?.Close(); } catch { }
                try { outboundClient?.Close(); } catch { }
                try { listener?.Stop(); } catch { }

                shutdownCts?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError("[PeakGhosts] Shutdown error: " + e);
            }
        }

        void Update()
        {
            // Try to find local player if we don't have it yet (heuristic)
            if (localPlayer == null)
            {
                var objs = GameObject.FindGameObjectsWithTag("Player");
                if (objs.Length > 0) localPlayer = objs[0];
                // alternative searches can be added here
            }

            // Send our pose at rate
            if (Time.time - lastSend >= sendInterval &&
                outboundClient != null &&
                outboundClient.Connected &&
                localPlayer != null)
            {
                lastSend = Time.time;
                var p = localPlayer.transform.position;
                var r = localPlayer.transform.rotation.eulerAngles;
                var msg = $"POSE|{myId}|{p.x:F3},{p.y:F3},{p.z:F3}|{r.x:F2},{r.y:F2},{r.z:F2}\n";

                try
                {
                    if (outboundStream != null)
                    {
                        var data = Encoding.UTF8.GetBytes(msg);
                        outboundStream.Write(data, 0, data.Length);
                        outboundStream.Flush();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PeakGhosts] Failed to send pose: " + e.Message);
                }
            }

            // Smoothly move ghosts towards target
            var remove = new List<string>();

            lock (ghostsLock)
            {
                foreach (var kv in ghosts)
                {
                    var g = kv.Value;
                    if (g.go == null)
                    {
                        remove.Add(kv.Key);
                        continue;
                    }

                    // despawn if stale (no updates for 30s)
                    if (Time.time - g.lastUpdateTime > 30f)
                    {
                        Destroy(g.go);
                        remove.Add(kv.Key);
                        continue;
                    }

                    g.go.transform.position = Vector3.Lerp(g.go.transform.position, g.targetPos, Time.deltaTime * 8f);
                    g.go.transform.rotation = Quaternion.Slerp(g.go.transform.rotation, g.targetRot, Time.deltaTime * 8f);
                }

                foreach (var id in remove)
                {
                    ghosts.Remove(id);
                }
            }

            // Apply voice mute heuristic: mute audio sources whose parent/owner indicates "remote player"
            // NOTE: adjust naming logic to match the game's player object names
            try
            {
                // Use reflection to handle different Unity versions
                var objectType = typeof(UnityEngine.Object);
                var findMethod = objectType.GetMethod("FindObjectsByType", new[] { typeof(Type), typeof(int) }) ??
                                objectType.GetMethod("FindObjectsOfType", new[] { typeof(Type) });

                if (findMethod != null)
                {
                    UnityEngine.Object[] audioSources;
                    if (findMethod.Name == "FindObjectsByType")
                    {
                        audioSources = (UnityEngine.Object[])findMethod.Invoke(null, new object[] { typeof(AudioSource), 0 });
                    }
                    else
                    {
                        audioSources = (UnityEngine.Object[])findMethod.Invoke(null, new object[] { typeof(AudioSource) });
                    }

                    foreach (var obj in audioSources)
                    {
                        var audioSource = obj as AudioSource;
                        if (audioSource?.gameObject == null) continue;

                        var go = audioSource.gameObject;
                        // crude heuristic: we consider sources with "Player" in name as player audio
                        if (go.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (go.transform.parent != null &&
                             go.transform.parent.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            audioSource.mute = muteVC;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] Error applying voice mute: " + e.Message);
            }
        }

        // ---------- Networking: listener (accept) and per-client handler ----------
        private async void StartListener(CancellationToken ct)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, relayPort);
                listener.Start();
                Debug.Log($"[PeakGhosts] Relay listener started on port {relayPort}");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await AcceptTcpClientAsync(listener, ct);
                        if (client != null)
                        {
                            lock (clientsLock)
                            {
                                connectedClients.Add(client);
                            }
                            Task.Run(() => HandleInboundClient(client, ct), ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[PeakGhosts] Accept client error: " + e.Message);
                        await Task.Delay(1000, ct);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] Listener stopped or failed: " + e);
            }
        }

        private async Task<TcpClient> AcceptTcpClientAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (listener.Pending())
                {
                    return listener.AcceptTcpClient();
                }
                await Task.Delay(50, ct);
            }
            return null;
        }

        private void HandleInboundClient(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    var buf = new byte[4096];
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        if (!ns.DataAvailable)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        int read = ns.Read(buf, 0, buf.Length);
                        if (read <= 0) break;

                        var msg = Encoding.UTF8.GetString(buf, 0, read);
                        // Could be multiple messages; split by newline
                        var parts = msg.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            var trimmed = p.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                ProcessInboundMessage(trimmed);
                            }
                        }

                        // broadcast to outbound (so every connected client receives messages via outbound client)
                        BroadcastToOutbound(msg);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] inbound client handler error: " + e.Message);
            }
            finally
            {
                lock (clientsLock)
                {
                    connectedClients.Remove(client);
                }
            }
        }

        private void BroadcastToOutbound(string msg)
        {
            try
            {
                if (outboundClient?.Connected == true && outboundStream != null)
                {
                    var data = Encoding.UTF8.GetBytes(msg);
                    outboundStream.Write(data, 0, data.Length);
                    outboundStream.Flush();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] Broadcast error: " + e.Message);
            }
        }

        // ---------- Outbound: connect to an existing relay (could be same host or VPS) ----------
        private async void StartOutboundConnectionLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (outboundClient?.Connected != true)
                    {
                        try { outboundClient?.Close(); } catch { }

                        outboundClient = new TcpClient();
                        await ConnectAsync(outboundClient, relayHost, relayPort, ct);
                        outboundStream = outboundClient.GetStream();

                        Debug.Log($"[PeakGhosts] Connected outbound to relay {relayHost}:{relayPort}");

                        // Start read-loop for inbound messages from relay
                        Task.Run(() => OutboundReadLoop(outboundClient, outboundStream, ct), ct);
                    }

                    await Task.Delay(5000, ct); // Check connection every 5 seconds
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PeakGhosts] outbound connect failed: " + e.Message);
                    try { outboundClient?.Close(); } catch { }
                    outboundClient = null;
                    outboundStream = null;

                    try
                    {
                        await Task.Delay(2000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task ConnectAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            var connectTask = Task.Run(() => client.Connect(host, port), ct);
            var timeoutTask = Task.Delay(5000, ct);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                client.Close();
                throw new TimeoutException("Connection timeout");
            }

            await connectTask; // Re-await to get any exceptions
        }

        private void OutboundReadLoop(TcpClient client, NetworkStream ns, CancellationToken ct)
        {
            var buf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    if (!ns.DataAvailable)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int len = ns.Read(buf, 0, buf.Length);
                    if (len <= 0) break;

                    var msg = Encoding.UTF8.GetString(buf, 0, len);
                    var split = msg.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var m in split)
                    {
                        var trimmed = m.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            ProcessInboundMessage(trimmed);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] outbound read loop ended: " + e.Message);
            }
            finally
            {
                try { client?.Close(); } catch { }
            }
        }

        // ---------- Message processing ----------
        // Format: POSE|peerId|x,y,z|rx,ry,rz
        private void ProcessInboundMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            try
            {
                if (!msg.StartsWith("POSE|")) return;

                var parts = msg.Split('|');
                if (parts.Length < 4) return;

                var id = parts[1];
                if (id == myId) return; // Don't process our own messages

                var pos = ParseVec3(parts[2]);
                var rot = Quaternion.Euler(ParseVec3(parts[3]));

                lock (ghostsLock)
                {
                    if (!ghosts.TryGetValue(id, out var g))
                    {
                        g = new Ghost
                        {
                            id = id,
                            go = CreateGhostObject(),
                            targetPos = pos,
                            targetRot = rot,
                            lastUpdateTime = Time.time
                        };
                        ghosts[id] = g;
                    }
                    else
                    {
                        g.targetPos = pos;
                        g.targetRot = rot;
                        g.lastUpdateTime = Time.time;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PeakGhosts] ProcessInboundMessage failed: " + e.Message);
            }
        }

        private Vector3 ParseVec3(string csv)
        {
            var s = csv.Split(',');
            float x = 0, y = 0, z = 0;
            if (s.Length > 0) float.TryParse(s[0], out x);
            if (s.Length > 1) float.TryParse(s[1], out y);
            if (s.Length > 2) float.TryParse(s[2], out z);
            return new Vector3(x, y, z);
        }

        private GameObject CreateGhostObject()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "GhostPlayer";
            go.transform.localScale = Vector3.one * 0.9f;

            // Apply the ghostMaterial to all renderers
            var renderers = go.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r != null) r.sharedMaterial = ghostMaterial;
            }

            // disable colliders so ghosts don't interact
            var colliders = go.GetComponentsInChildren<Collider>();
            foreach (var c in colliders)
            {
                if (c != null) Destroy(c);
            }

            return go;
        }

        private Material BuildGhostMaterial()
        {
            var shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning("[PeakGhosts] Standard shader not found, using Legacy/Transparent/Diffuse");
                shader = Shader.Find("Legacy/Transparent/Diffuse");
            }

            if (shader == null)
            {
                Debug.LogWarning("[PeakGhosts] No suitable transparent shader found, using default");
                shader = Shader.Find("Diffuse");
            }

            var m = new Material(shader);

            // Try to set up transparency if we have a Standard shader
            if (shader.name == "Standard")
            {
                try
                {
                    m.SetFloat("_Mode", 3f); // Transparent mode
                    m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    m.DisableKeyword("_ALPHATEST_ON");
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = 3000;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PeakGhosts] Failed to configure Standard shader transparency: " + e.Message);
                }
            }

            // Set color with transparency
            if (m.HasProperty("_Color"))
            {
                m.color = new Color(0.8f, 0.9f, 1f, 0.25f); // pale bluish ghost
            }
            else if (m.HasProperty("_MainColor"))
            {
                m.SetColor("_MainColor", new Color(0.8f, 0.9f, 1f, 0.25f));
            }

            return m;
        }

        void OnDestroy()
        {
            Shutdown();
        }
    }
}
