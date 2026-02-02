using System.Collections.Concurrent;
using System.IO;
using System.Net;
using MessagePack;
using SyncBeam.P2P.Core;
using SyncBeam.P2P.Discovery;
using SyncBeam.P2P.Transport;

namespace SyncBeam.P2P;

/// <summary>
/// Central manager for all P2P operations.
/// Coordinates discovery, connections, and message routing.
/// Auto-connects to discovered peers on the same LAN.
/// Scans network to show all devices.
/// </summary>
public sealed class PeerManager : IDisposable
{
    private readonly PeerIdentity _localIdentity;
    private readonly MdnsDiscovery _discovery;
    private readonly NetworkScanner _networkScanner;
    private readonly ConnectionListener _listener;
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _discoveredEndpoints = new();
    private readonly ConcurrentDictionary<string, bool> _connectingPeers = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string LocalPeerId => _localIdentity.PeerId;
    public int ListenPort => _listener.Port;
    public IReadOnlyDictionary<string, ConnectedPeer> ConnectedPeers => _peers;
    public IReadOnlyDictionary<string, NetworkDevice> NetworkDevices => _networkScanner.Devices;

    public event EventHandler<PeerEventArgs>? PeerConnected;
    public event EventHandler<PeerEventArgs>? PeerDisconnected;
    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;
    public event EventHandler<PeerConnectionFailedEventArgs>? PeerConnectionFailed;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<NetworkDeviceEventArgs>? NetworkDeviceDiscovered;
    public event EventHandler? NetworkScanCompleted;

    private const int DefaultPort = 42420;

    public PeerManager(int listenPort = DefaultPort)
    {
        _localIdentity = PeerIdentity.Generate();

        _listener = new ConnectionListener(_localIdentity, listenPort);
        _listener.PeerConnected += OnIncomingConnection;
        _listener.ConnectionFailed += OnConnectionFailed;

        _discovery = new MdnsDiscovery(_localIdentity, _listener.Port);
        _discovery.PeerDiscovered += OnPeerDiscovered;
        _discovery.PeerLost += OnPeerLost;

        _networkScanner = new NetworkScanner();
        _networkScanner.DeviceDiscovered += OnNetworkDeviceDiscovered;
        _networkScanner.ScanCompleted += (_, _) => NetworkScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        _listener.Start();
        _discovery.Start();
        _networkScanner.StartScan();
    }

    /// <summary>
    /// Rescan the network for all devices.
    /// </summary>
    public void ScanNetwork()
    {
        _networkScanner.StartScan();
    }

    public void Stop()
    {
        _cts.Cancel();
        _networkScanner.StopScan();
        _discovery.Stop();
        _listener.Stop();

        foreach (var peer in _peers.Values)
        {
            peer.Dispose();
        }
        _peers.Clear();
    }

    /// <summary>
    /// Connect to a discovered peer by ID.
    /// </summary>
    public async Task<bool> ConnectToPeerAsync(string peerId)
    {
        if (_peers.ContainsKey(peerId))
            return true; // Already connected

        // Prevent concurrent connection attempts to the same peer
        if (!_connectingPeers.TryAdd(peerId, true))
            return false;

        string? errorMessage = null;
        try
        {
            // First try to get endpoint from mDNS discovery
            if (!_discoveredEndpoints.TryGetValue(peerId, out var endpoint))
            {
                // Try to find the endpoint from network scanner by peerId
                var device = _networkScanner.Devices.Values
                    .FirstOrDefault(d => d.SyncBeamPeerId == peerId);

                if (device != null)
                {
                    // Use default SyncBeam port (the listener port)
                    endpoint = new IPEndPoint(device.IpAddress, _listener.Port);
                    _discoveredEndpoints.TryAdd(peerId, endpoint);
                }
                else
                {
                    errorMessage = "Peer endpoint not found. Try scanning the network first.";
                    return false;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connecting to {peerId} at {endpoint}");

            var transport = await ConnectionFactory.ConnectAsync(
                endpoint, _localIdentity, _cts.Token);

            var remotePeerId = transport.RemotePeer?.PeerId ?? peerId;
            var peer = new ConnectedPeer(transport, false);

            if (_peers.TryAdd(remotePeerId, peer))
            {
                peer.Disconnected += (_, _) => OnPeerDisconnected(remotePeerId);
                peer.MessageReceived += (_, e) => OnMessageReceived(remotePeerId, e);
                peer.StartReceiving(_cts.Token);

                PeerConnected?.Invoke(this, new PeerEventArgs { PeerId = remotePeerId, Peer = peer });
                return true;
            }
            else
            {
                transport.Dispose();
                errorMessage = "Peer already connected";
            }
        }
        catch (TimeoutException ex)
        {
            errorMessage = "Connection timed out. Make sure the device is running SyncBeam.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection timeout to {peerId}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Connection was canceled.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection canceled to {peerId}");
        }
        catch (EndOfStreamException ex)
        {
            errorMessage = "Remote device closed connection. Make sure SyncBeam is running on both devices.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection closed by remote {peerId}: {ex.Message}");
        }
        catch (System.IO.IOException ex)
        {
            errorMessage = ex.InnerException?.Message ?? ex.Message;
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection IO error to {peerId}: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            errorMessage = $"Network error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Socket error to {peerId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            errorMessage = $"Connection failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection failed to {peerId}: {ex.Message}");
        }
        finally
        {
            _connectingPeers.TryRemove(peerId, out _);

            // Notify UI of connection failure
            if (errorMessage != null)
            {
                PeerConnectionFailed?.Invoke(this, new PeerConnectionFailedEventArgs
                {
                    PeerId = peerId,
                    ErrorMessage = errorMessage
                });
            }
        }

        return false;
    }

    /// <summary>
    /// Connect to a peer by IP address.
    /// </summary>
    public async Task<bool> ConnectToIpAsync(string ipAddress, int? port = null)
    {
        var targetPort = port ?? _listener.Port;
        var connectionId = $"ip-{ipAddress}";

        if (!_connectingPeers.TryAdd(connectionId, true))
            return false;

        string? errorMessage = null;
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                errorMessage = "Invalid IP address";
                return false;
            }

            var endpoint = new IPEndPoint(ip, targetPort);
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connecting to IP {endpoint}");

            var transport = await ConnectionFactory.ConnectAsync(
                endpoint, _localIdentity, _cts.Token);

            var peerId = transport.RemotePeer?.PeerId ?? connectionId;
            var peer = new ConnectedPeer(transport, false);

            if (_peers.TryAdd(peerId, peer))
            {
                _discoveredEndpoints.TryAdd(peerId, endpoint);
                peer.Disconnected += (_, _) => OnPeerDisconnected(peerId);
                peer.MessageReceived += (_, e) => OnMessageReceived(peerId, e);
                peer.StartReceiving(_cts.Token);

                // Mark as SyncBeam device
                _networkScanner.MarkAsSyncBeamDevice(ip, peerId);

                PeerConnected?.Invoke(this, new PeerEventArgs { PeerId = peerId, Peer = peer });
                return true;
            }
            else
            {
                transport.Dispose();
                errorMessage = "Peer already connected";
            }
        }
        catch (TimeoutException ex)
        {
            errorMessage = "Connection timed out. Make sure the device is running SyncBeam.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection timeout to IP {ipAddress}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Connection was canceled.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection canceled to IP {ipAddress}");
        }
        catch (EndOfStreamException ex)
        {
            errorMessage = "Remote device closed connection. Make sure SyncBeam is running on both devices.";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection closed by remote IP {ipAddress}: {ex.Message}");
        }
        catch (IOException ex)
        {
            errorMessage = ex.InnerException?.Message ?? ex.Message;
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection IO error to IP {ipAddress}: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            errorMessage = $"Network error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Socket error to IP {ipAddress}: {ex.Message}");
        }
        catch (Exception ex)
        {
            errorMessage = $"Connection failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Connection to IP {ipAddress} failed: {ex.Message}");
        }
        finally
        {
            _connectingPeers.TryRemove(connectionId, out _);

            if (errorMessage != null)
            {
                PeerConnectionFailed?.Invoke(this, new PeerConnectionFailedEventArgs
                {
                    PeerId = connectionId,
                    ErrorMessage = errorMessage
                });
            }
        }

        return false;
    }

    /// <summary>
    /// Send a message to a specific peer.
    /// </summary>
    public async Task<bool> SendAsync<T>(string peerId, MessageType type, T message)
    {
        if (!_peers.TryGetValue(peerId, out var peer))
            return false;

        var payload = MessagePackSerializer.Serialize(message);
        await peer.SendAsync(type, payload, _cts.Token);
        return true;
    }

    /// <summary>
    /// Broadcast a message to all connected peers.
    /// </summary>
    public async Task BroadcastAsync<T>(MessageType type, T message)
    {
        var payload = MessagePackSerializer.Serialize(message);
        var tasks = _peers.Values.Select(p => p.SendAsync(type, payload, _cts.Token));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Refresh peer discovery.
    /// </summary>
    public void RefreshDiscovery()
    {
        _discovery.QueryPeers();
    }

    private void OnIncomingConnection(object? sender, PeerConnectedEventArgs e)
    {
        var peerId = e.RemotePeer.PeerId;

        System.Diagnostics.Debug.WriteLine($"[PeerManager] Incoming connection from {peerId}");

        // Cancel any outgoing connection attempt to this peer
        _connectingPeers.TryRemove(peerId, out _);

        if (_peers.ContainsKey(peerId))
        {
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Already connected to {peerId}, rejecting duplicate");
            e.Transport.Dispose();
            return;
        }

        var peer = new ConnectedPeer(e.Transport, true);
        if (_peers.TryAdd(peerId, peer))
        {
            peer.Disconnected += (_, _) => OnPeerDisconnected(peerId);
            peer.MessageReceived += (_, args) => OnMessageReceived(peerId, args);
            peer.StartReceiving(_cts.Token);

            _discoveredEndpoints.TryAdd(peerId, e.Endpoint);
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Successfully connected (incoming) to {peerId}");
            PeerConnected?.Invoke(this, new PeerEventArgs { PeerId = peerId, Peer = peer });
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Failed to add peer {peerId}, disposing");
            e.Transport.Dispose();
        }
    }

    private void OnConnectionFailed(object? sender, PeerConnectionFailedEventArgs e)
    {
        // Log or handle connection failures
    }

    private void OnPeerDiscovered(object? sender, DiscoveredPeerEventArgs e)
    {
        _discoveredEndpoints.TryAdd(e.PeerId, e.Endpoint);

        // Mark this device as having SyncBeam in the network scanner
        _networkScanner.MarkAsSyncBeamDevice(e.Endpoint.Address, e.PeerId);

        System.Diagnostics.Debug.WriteLine($"[PeerManager] Discovered peer {e.PeerId} at {e.Endpoint}");

        PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs
        {
            PeerId = e.PeerId,
            Endpoint = e.Endpoint
        });

        // Auto-connect to discovered peers (with a small delay to avoid race conditions)
        if (!_peers.ContainsKey(e.PeerId) && !_connectingPeers.ContainsKey(e.PeerId))
        {
            System.Diagnostics.Debug.WriteLine($"[PeerManager] Auto-connecting to {e.PeerId}");
            _ = Task.Run(async () =>
            {
                // Small random delay to reduce simultaneous connection attempts
                await Task.Delay(Random.Shared.Next(100, 500));
                if (!_peers.ContainsKey(e.PeerId) && !_connectingPeers.ContainsKey(e.PeerId))
                {
                    await ConnectToPeerAsync(e.PeerId);
                }
            });
        }
    }

    private void OnNetworkDeviceDiscovered(object? sender, NetworkDeviceEventArgs e)
    {
        NetworkDeviceDiscovered?.Invoke(this, e);
    }

    private void OnPeerLost(object? sender, DiscoveredPeerEventArgs e)
    {
        _discoveredEndpoints.TryRemove(e.PeerId, out _);
    }

    private void OnPeerDisconnected(string peerId)
    {
        if (_peers.TryRemove(peerId, out var peer))
        {
            peer.Dispose();
            PeerDisconnected?.Invoke(this, new PeerEventArgs { PeerId = peerId, Peer = peer });
        }
    }

    private void OnMessageReceived(string peerId, MessageReceivedEventArgs e)
    {
        MessageReceived?.Invoke(this, new MessageReceivedEventArgs
        {
            PeerId = peerId,
            Type = e.Type,
            Payload = e.Payload
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _discovery.Dispose();
            _listener.Dispose();
            _localIdentity.Dispose();
            _cts.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents a connected peer with message handling.
/// </summary>
public sealed class ConnectedPeer : IDisposable
{
    private readonly SecureTransport _transport;
    private Task? _receiveTask;
    private bool _disposed;

    public RemotePeerIdentity RemotePeer => _transport.RemotePeer!;
    public bool IsIncoming { get; }
    public bool IsConnected => _transport.IsConnected;

    public event EventHandler? Disconnected;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    internal ConnectedPeer(SecureTransport transport, bool isIncoming)
    {
        _transport = transport;
        IsIncoming = isIncoming;
    }

    internal void StartReceiving(CancellationToken ct)
    {
        _receiveTask = ReceiveLoopAsync(ct);
    }

    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        await _transport.SendAsync(type, payload, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _transport.IsConnected)
            {
                var (type, payload) = await _transport.ReceiveAsync(ct);

                // Handle ping/pong internally
                if (type == MessageType.Ping)
                {
                    var ping = MessagePackSerializer.Deserialize<PingMessage>(payload);
                    var pong = new PongMessage
                    {
                        PingTimestamp = ping.Timestamp,
                        SequenceNumber = ping.SequenceNumber
                    };
                    await _transport.SendAsync(MessageType.Pong,
                        MessagePackSerializer.Serialize(pong), ct);
                    continue;
                }

                MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                {
                    Type = type,
                    Payload = payload
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Connection error
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _transport.Dispose();
            _disposed = true;
        }
    }
}

public class PeerEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required ConnectedPeer Peer { get; init; }
}

public class PeerDiscoveredEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required IPEndPoint Endpoint { get; init; }
}

public class MessageReceivedEventArgs : EventArgs
{
    public string? PeerId { get; init; }
    public required MessageType Type { get; init; }
    public required byte[] Payload { get; init; }
}
