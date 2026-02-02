using System.Net;
using System.Net.Sockets;
using SyncBeam.P2P.Core;

namespace SyncBeam.P2P.Transport;

/// <summary>
/// TCP listener for incoming peer connections.
/// </summary>
public sealed class ConnectionListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly PeerIdentity _localIdentity;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    public int Port { get; }
    public bool IsListening { get; private set; }

    public event EventHandler<PeerConnectedEventArgs>? PeerConnected;
    public event EventHandler<PeerConnectionFailedEventArgs>? ConnectionFailed;

    public ConnectionListener(PeerIdentity localIdentity, int port = 0)
    {
        _localIdentity = localIdentity;

        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _listener.Stop();
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Will listen on port {Port}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Failed to bind to port {port}: {ex.Message}");
            // Try with a random port if the specified port is in use
            if (port != 0)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Trying random port...");
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _listener.Stop();
                System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Using fallback port {Port}");
            }
            else
            {
                throw;
            }
        }
    }

    public void Start()
    {
        if (IsListening) return;

        _cts = new CancellationTokenSource();

        try
        {
            _listener.Start();
            IsListening = true;
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Started listening on port {Port}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Failed to start listener on port {Port}: {ex.Message}");
            throw new InvalidOperationException($"Cannot start listener on port {Port}. Port may be in use by another application.", ex);
        }

        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsListening) return;

        _cts?.Cancel();
        _listener.Stop();
        IsListening = false;

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleIncomingConnectionAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionFailed?.Invoke(this, new PeerConnectionFailedEventArgs
                {
                    Error = ex,
                    Endpoint = null
                });
            }
        }
    }

    private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = (IPEndPoint?)client.Client.RemoteEndPoint;
        SecureTransport? transport = null;

        System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Incoming connection from {endpoint}");

        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 30000;

            transport = new SecureTransport(client, _localIdentity);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));

            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Starting handshake with {endpoint}");
            await transport.HandshakeAsResponderAsync(handshakeCts.Token);
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Handshake complete with {endpoint}");

            PeerConnected?.Invoke(this, new PeerConnectedEventArgs
            {
                Transport = transport,
                RemotePeer = transport.RemotePeer!,
                Endpoint = endpoint!,
                IsIncoming = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionListener] Connection failed from {endpoint}: {ex.Message}");
            transport?.Dispose();
            ConnectionFailed?.Invoke(this, new PeerConnectionFailedEventArgs
            {
                Error = ex,
                Endpoint = endpoint,
                ErrorMessage = ex.Message
            });
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Outbound connection factory.
/// </summary>
public static class ConnectionFactory
{
    public static async Task<SecureTransport> ConnectAsync(
        IPEndPoint endpoint,
        PeerIdentity localIdentity,
        CancellationToken ct = default)
    {
        var client = new TcpClient();
        SecureTransport? transport = null;

        try
        {
            // Configure TCP client for better connection reliability
            client.NoDelay = true;
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 30000;

            // Use a dedicated timeout for connection (not linked to parent token)
            // This prevents "operation was canceled" when parent token is just for cleanup
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {
                await client.ConnectAsync(endpoint.Address, endpoint.Port, connectCts.Token);
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Connection to {endpoint} timed out after 15 seconds");
            }

            // Check if parent cancellation was requested
            ct.ThrowIfCancellationRequested();

            transport = new SecureTransport(client, localIdentity);

            // Handshake timeout
            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try
            {
                await transport.HandshakeAsInitiatorAsync(handshakeCts.Token);
            }
            catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Handshake with {endpoint} timed out after 30 seconds");
            }

            return transport;
        }
        catch (SocketException ex)
        {
            transport?.Dispose();
            client.Dispose();
            throw new IOException($"Could not connect to {endpoint}: {ex.Message}", ex);
        }
        catch
        {
            transport?.Dispose();
            client.Dispose();
            throw;
        }
    }
}

public class PeerConnectedEventArgs : EventArgs
{
    public required SecureTransport Transport { get; init; }
    public required RemotePeerIdentity RemotePeer { get; init; }
    public required IPEndPoint Endpoint { get; init; }
    public required bool IsIncoming { get; init; }
}

public class PeerConnectionFailedEventArgs : EventArgs
{
    public Exception? Error { get; init; }
    public IPEndPoint? Endpoint { get; init; }
    public string? PeerId { get; init; }
    public string? ErrorMessage { get; init; }
}
