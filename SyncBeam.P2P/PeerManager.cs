using System.Collections.Concurrent;
using System.Net;
using MessagePack;
using SyncBeam.P2P.Core;
using SyncBeam.P2P.Discovery;
using SyncBeam.P2P.Transport;

namespace SyncBeam.P2P;

/// <summary>
/// Central manager for all P2P operations.
/// Coordinates discovery, connections, and message routing.
/// </summary>
public sealed class PeerManager : IDisposable
{
    private readonly PeerIdentity _localIdentity;
    private readonly string _projectSecret;
    private readonly MdnsDiscovery _discovery;
    private readonly ConnectionListener _listener;
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _discoveredEndpoints = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string LocalPeerId => _localIdentity.PeerId;
    public int ListenPort => _listener.Port;
    public IReadOnlyDictionary<string, ConnectedPeer> ConnectedPeers => _peers;

    public event EventHandler<PeerEventArgs>? PeerConnected;
    public event EventHandler<PeerEventArgs>? PeerDisconnected;
    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public PeerManager(string projectSecret, int listenPort = 0)
    {
        _projectSecret = projectSecret;
        _localIdentity = PeerIdentity.Generate();

        _listener = new ConnectionListener(_localIdentity, projectSecret, listenPort);
        _listener.PeerConnected += OnIncomingConnection;
        _listener.ConnectionFailed += OnConnectionFailed;

        _discovery = new MdnsDiscovery(_localIdentity, projectSecret, _listener.Port);
        _discovery.PeerDiscovered += OnPeerDiscovered;
        _discovery.PeerLost += OnPeerLost;
    }

    public void Start()
    {
        _listener.Start();
        _discovery.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
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

        if (!_discoveredEndpoints.TryGetValue(peerId, out var endpoint))
            return false;

        try
        {
            var transport = await ConnectionFactory.ConnectAsync(
                endpoint, _localIdentity, _projectSecret, _cts.Token);

            var peer = new ConnectedPeer(transport, false);
            if (_peers.TryAdd(peerId, peer))
            {
                peer.Disconnected += (_, _) => OnPeerDisconnected(peerId);
                peer.MessageReceived += (_, e) => OnMessageReceived(peerId, e);
                peer.StartReceiving(_cts.Token);

                PeerConnected?.Invoke(this, new PeerEventArgs { PeerId = peerId, Peer = peer });
                return true;
            }
            else
            {
                transport.Dispose();
            }
        }
        catch
        {
            // Connection failed
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

        if (_peers.ContainsKey(peerId))
        {
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
            PeerConnected?.Invoke(this, new PeerEventArgs { PeerId = peerId, Peer = peer });
        }
        else
        {
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
        PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs
        {
            PeerId = e.PeerId,
            Endpoint = e.Endpoint
        });
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
