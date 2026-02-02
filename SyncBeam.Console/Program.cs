using System.Text;
using MessagePack;
using SyncBeam.P2P;
using SyncBeam.P2P.Transport;

namespace SyncBeam.Console;

/// <summary>
/// Phase 1 Test Console - Discovery, Handshake, and Encrypted Channel
/// </summary>
class Program
{
    private static readonly string DefaultSecret = "SyncBeam-Test-Secret-2024";

    static async Task Main(string[] args)
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║              SyncBeam P2P Test Console - Phase 1              ║");
        System.Console.WriteLine("║         Discovery • Handshake • Encrypted Channel            ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        var secret = args.Length > 0 ? args[0] : DefaultSecret;
        System.Console.WriteLine($"[*] Project Secret: {secret[..Math.Min(10, secret.Length)]}...");

        using var manager = new PeerManager(secret);

        // Wire up events
        manager.PeerDiscovered += (_, e) =>
        {
            System.Console.WriteLine($"[DISCOVERED] Peer {e.PeerId[..8]}... at {e.Endpoint}");
        };

        manager.PeerConnected += (_, e) =>
        {
            var direction = e.Peer.IsIncoming ? "incoming" : "outgoing";
            System.Console.WriteLine($"[CONNECTED] Peer {e.PeerId[..8]}... ({direction})");
            System.Console.WriteLine($"            Remote Public Key: {Convert.ToHexString(e.Peer.RemotePeer.PublicKeyBytes[..8])}...");
        };

        manager.PeerDisconnected += (_, e) =>
        {
            System.Console.WriteLine($"[DISCONNECTED] Peer {e.PeerId[..8]}...");
        };

        manager.MessageReceived += (_, e) =>
        {
            HandleMessage(e);
        };

        // Start the manager
        manager.Start();

        System.Console.WriteLine();
        System.Console.WriteLine($"[*] Local Peer ID: {manager.LocalPeerId}");
        System.Console.WriteLine($"[*] Listening on port: {manager.ListenPort}");
        System.Console.WriteLine();
        System.Console.WriteLine("Commands:");
        System.Console.WriteLine("  list     - List discovered peers");
        System.Console.WriteLine("  connect  - Connect to a discovered peer");
        System.Console.WriteLine("  peers    - Show connected peers");
        System.Console.WriteLine("  send     - Send a test message");
        System.Console.WriteLine("  ping     - Ping all connected peers");
        System.Console.WriteLine("  refresh  - Refresh peer discovery");
        System.Console.WriteLine("  quit     - Exit");
        System.Console.WriteLine();

        // Command loop
        while (true)
        {
            System.Console.Write("> ");
            var line = System.Console.ReadLine()?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(line))
                continue;

            try
            {
                switch (line)
                {
                    case "list":
                        ListDiscoveredPeers(manager);
                        break;

                    case "connect":
                        await ConnectToPeer(manager);
                        break;

                    case "peers":
                        ListConnectedPeers(manager);
                        break;

                    case "send":
                        await SendTestMessage(manager);
                        break;

                    case "ping":
                        await PingPeers(manager);
                        break;

                    case "refresh":
                        manager.RefreshDiscovery();
                        System.Console.WriteLine("[*] Discovery refresh sent");
                        break;

                    case "quit":
                    case "exit":
                    case "q":
                        System.Console.WriteLine("[*] Shutting down...");
                        return;

                    default:
                        System.Console.WriteLine($"Unknown command: {line}");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] {ex.Message}");
            }
        }
    }

    private static void ListDiscoveredPeers(PeerManager manager)
    {
        var discovered = new List<string>();
        var field = typeof(PeerManager).GetField("_discoveredEndpoints",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var endpoints = field?.GetValue(manager) as System.Collections.Concurrent.ConcurrentDictionary<string, System.Net.IPEndPoint>;

        if (endpoints == null || endpoints.IsEmpty)
        {
            System.Console.WriteLine("[*] No peers discovered yet");
            return;
        }

        System.Console.WriteLine($"[*] Discovered peers ({endpoints.Count}):");
        int i = 1;
        foreach (var (peerId, endpoint) in endpoints)
        {
            var connected = manager.ConnectedPeers.ContainsKey(peerId) ? " [CONNECTED]" : "";
            System.Console.WriteLine($"    {i++}. {peerId[..16]}... -> {endpoint}{connected}");
        }
    }

    private static void ListConnectedPeers(PeerManager manager)
    {
        if (manager.ConnectedPeers.Count == 0)
        {
            System.Console.WriteLine("[*] No connected peers");
            return;
        }

        System.Console.WriteLine($"[*] Connected peers ({manager.ConnectedPeers.Count}):");
        int i = 1;
        foreach (var (peerId, peer) in manager.ConnectedPeers)
        {
            var direction = peer.IsIncoming ? "incoming" : "outgoing";
            System.Console.WriteLine($"    {i++}. {peerId[..16]}... ({direction})");
        }
    }

    private static async Task ConnectToPeer(PeerManager manager)
    {
        var field = typeof(PeerManager).GetField("_discoveredEndpoints",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var endpoints = field?.GetValue(manager) as System.Collections.Concurrent.ConcurrentDictionary<string, System.Net.IPEndPoint>;

        if (endpoints == null || endpoints.IsEmpty)
        {
            System.Console.WriteLine("[*] No peers discovered. Try 'refresh' first.");
            return;
        }

        var peerList = endpoints.ToList();
        System.Console.WriteLine("[*] Select a peer to connect:");
        for (int i = 0; i < peerList.Count; i++)
        {
            var connected = manager.ConnectedPeers.ContainsKey(peerList[i].Key) ? " [CONNECTED]" : "";
            System.Console.WriteLine($"    {i + 1}. {peerList[i].Key[..16]}...{connected}");
        }

        System.Console.Write("Enter number: ");
        var input = System.Console.ReadLine();
        if (!int.TryParse(input, out var num) || num < 1 || num > peerList.Count)
        {
            System.Console.WriteLine("[*] Invalid selection");
            return;
        }

        var selectedPeerId = peerList[num - 1].Key;
        System.Console.WriteLine($"[*] Connecting to {selectedPeerId[..16]}...");

        if (await manager.ConnectToPeerAsync(selectedPeerId))
        {
            System.Console.WriteLine("[*] Connection successful!");
        }
        else
        {
            System.Console.WriteLine("[*] Connection failed");
        }
    }

    private static async Task SendTestMessage(PeerManager manager)
    {
        if (manager.ConnectedPeers.Count == 0)
        {
            System.Console.WriteLine("[*] No connected peers");
            return;
        }

        System.Console.Write("Enter message: ");
        var message = System.Console.ReadLine();
        if (string.IsNullOrEmpty(message))
            return;

        // Use clipboard data message for testing
        var clipboardMsg = new ClipboardDataMessage
        {
            ClipboardId = Guid.NewGuid().ToString(),
            ContentType = ClipboardContentType.Text,
            Data = Encoding.UTF8.GetBytes(message),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await manager.BroadcastAsync(MessageType.ClipboardData, clipboardMsg);
        System.Console.WriteLine($"[*] Message sent to {manager.ConnectedPeers.Count} peer(s)");
    }

    private static async Task PingPeers(PeerManager manager)
    {
        if (manager.ConnectedPeers.Count == 0)
        {
            System.Console.WriteLine("[*] No connected peers");
            return;
        }

        var ping = new PingMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = 1
        };

        await manager.BroadcastAsync(MessageType.Ping, ping);
        System.Console.WriteLine($"[*] Ping sent to {manager.ConnectedPeers.Count} peer(s)");
    }

    private static void HandleMessage(MessageReceivedEventArgs e)
    {
        switch (e.Type)
        {
            case MessageType.ClipboardData:
                var clipboardMsg = MessagePackSerializer.Deserialize<ClipboardDataMessage>(e.Payload);
                if (clipboardMsg.ContentType == ClipboardContentType.Text)
                {
                    var text = Encoding.UTF8.GetString(clipboardMsg.Data);
                    System.Console.WriteLine();
                    System.Console.WriteLine($"[MESSAGE from {e.PeerId?[..8]}...] {text}");
                    System.Console.Write("> ");
                }
                break;

            case MessageType.Pong:
                var pong = MessagePackSerializer.Deserialize<PongMessage>(e.Payload);
                var latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.PingTimestamp;
                System.Console.WriteLine();
                System.Console.WriteLine($"[PONG from {e.PeerId?[..8]}...] Latency: {latency}ms");
                System.Console.Write("> ");
                break;

            default:
                System.Console.WriteLine();
                System.Console.WriteLine($"[RECEIVED] Type: {e.Type}, Size: {e.Payload.Length} bytes");
                System.Console.Write("> ");
                break;
        }
    }
}
