using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SyncBeam.P2P.NatTraversal;

/// <summary>
/// Coordinates NAT traversal between peers using STUN and hole punching.
/// Helps establish direct connections when both peers are behind NAT.
/// </summary>
public class NatTraversalCoordinator : IDisposable
{
    private readonly int _localPort;
    private HolePuncher? _holePuncher;
    private bool _disposed;

    /// <summary>
    /// Gets the local port being used for hole punching.
    /// </summary>
    public int LocalPort => _holePuncher?.LocalPort ?? _localPort;

    /// <summary>
    /// Gets the discovered public endpoint.
    /// </summary>
    public IPEndPoint? PublicEndpoint => _holePuncher?.PublicEndpoint;

    /// <summary>
    /// Event raised when hole punching status changes.
    /// </summary>
    public event EventHandler<HolePunchStatusEventArgs>? StatusChanged;

    public NatTraversalCoordinator(int localPort = 0)
    {
        _localPort = localPort;
    }

    /// <summary>
    /// Initializes the coordinator and discovers public endpoint.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _holePuncher = new HolePuncher(_localPort);

            StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
            {
                Status = HolePunchStatus.DiscoveringEndpoint,
                Message = "Discovering public endpoint..."
            });

            var success = await _holePuncher.DiscoverPublicEndpointAsync(ct);

            if (success)
            {
                StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
                {
                    Status = HolePunchStatus.Ready,
                    Message = $"Public endpoint: {_holePuncher.PublicEndpoint}",
                    PublicEndpoint = _holePuncher.PublicEndpoint
                });
            }
            else
            {
                StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
                {
                    Status = HolePunchStatus.Failed,
                    Message = "Could not discover public endpoint"
                });
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NatTraversalCoordinator] Init failed: {ex.Message}");
            StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
            {
                Status = HolePunchStatus.Failed,
                Message = $"Initialization failed: {ex.Message}"
            });
            return false;
        }
    }

    /// <summary>
    /// Attempts to establish connectivity with a remote peer using hole punching.
    /// Both peers should call this simultaneously with each other's public endpoint.
    /// </summary>
    /// <param name="remoteEndpoint">The remote peer's public endpoint</param>
    /// <param name="timeout">Maximum time to attempt hole punching</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result of the hole punch attempt</returns>
    public async Task<HolePunchResult> PunchHoleAsync(
        IPEndPoint remoteEndpoint,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(10);

        if (_holePuncher == null)
        {
            return new HolePunchResult
            {
                Success = false,
                ErrorMessage = "Coordinator not initialized. Call InitializeAsync first."
            };
        }

        try
        {
            StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
            {
                Status = HolePunchStatus.Punching,
                Message = $"Punching hole to {remoteEndpoint}...",
                RemoteEndpoint = remoteEndpoint
            });

            var success = await _holePuncher.PunchHoleAsync(remoteEndpoint, timeout.Value, ct);

            if (success)
            {
                StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
                {
                    Status = HolePunchStatus.Connected,
                    Message = $"Hole punched to {remoteEndpoint}",
                    RemoteEndpoint = remoteEndpoint
                });

                return new HolePunchResult
                {
                    Success = true,
                    LocalEndpoint = new IPEndPoint(IPAddress.Any, LocalPort),
                    RemoteEndpoint = remoteEndpoint
                };
            }
            else
            {
                StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
                {
                    Status = HolePunchStatus.Failed,
                    Message = $"Failed to punch hole to {remoteEndpoint}",
                    RemoteEndpoint = remoteEndpoint
                });

                return new HolePunchResult
                {
                    Success = false,
                    ErrorMessage = "Hole punching timed out"
                };
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
            {
                Status = HolePunchStatus.Failed,
                Message = $"Hole punching failed: {ex.Message}",
                RemoteEndpoint = remoteEndpoint
            });

            return new HolePunchResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Performs a coordinated hole punch where both peers exchange endpoints
    /// and attempt simultaneous punching.
    /// </summary>
    /// <param name="exchangeEndpoint">Function to exchange endpoints with the remote peer</param>
    /// <param name="timeout">Maximum time for the operation</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result of the coordinated hole punch</returns>
    public async Task<HolePunchResult> CoordinatedPunchAsync(
        Func<IPEndPoint, Task<IPEndPoint?>> exchangeEndpoint,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        if (_holePuncher?.PublicEndpoint == null)
        {
            return new HolePunchResult
            {
                Success = false,
                ErrorMessage = "Local public endpoint not discovered"
            };
        }

        try
        {
            StatusChanged?.Invoke(this, new HolePunchStatusEventArgs
            {
                Status = HolePunchStatus.ExchangingEndpoints,
                Message = "Exchanging endpoints with peer..."
            });

            // Exchange endpoints with remote peer
            var remoteEndpoint = await exchangeEndpoint(_holePuncher.PublicEndpoint);

            if (remoteEndpoint == null)
            {
                return new HolePunchResult
                {
                    Success = false,
                    ErrorMessage = "Failed to receive remote endpoint"
                };
            }

            // Perform the hole punch
            return await PunchHoleAsync(remoteEndpoint, timeout, ct);
        }
        catch (Exception ex)
        {
            return new HolePunchResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Creates a UDP socket that can be used after successful hole punching.
    /// </summary>
    public UdpClient? CreatePunchedSocket()
    {
        if (_holePuncher == null)
            return null;

        // Create a new UDP client bound to the same port
        try
        {
            return new UdpClient(LocalPort);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tests if direct UDP communication is possible with a peer.
    /// </summary>
    public async Task<bool> TestDirectConnectivityAsync(
        IPEndPoint remoteEndpoint,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(5);

        try
        {
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = (int)timeout.Value.TotalMilliseconds;

            var testPacket = new byte[] { 0x54, 0x45, 0x53, 0x54 }; // "TEST"
            var ackPacket = new byte[] { 0x41, 0x43, 0x4B, 0x21 }; // "ACK!"

            // Send test packet
            await client.SendAsync(testPacket, remoteEndpoint, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout.Value);

            // Wait for response
            try
            {
                var result = await client.ReceiveAsync(timeoutCts.Token);

                if (result.Buffer.Length >= 4 &&
                    result.Buffer[0] == 0x41 && // 'A'
                    result.Buffer[1] == 0x43)   // 'C'
                {
                    return true;
                }

                // If we got the TEST packet, send ACK back
                if (result.Buffer.Length >= 4 &&
                    result.Buffer[0] == 0x54 && // 'T'
                    result.Buffer[1] == 0x45)   // 'E'
                {
                    await client.SendAsync(ackPacket, result.RemoteEndPoint, ct);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout - no response
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NatTraversalCoordinator] Connectivity test failed: {ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _holePuncher?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Status of hole punching operation.
/// </summary>
public enum HolePunchStatus
{
    NotStarted,
    DiscoveringEndpoint,
    Ready,
    ExchangingEndpoints,
    Punching,
    Connected,
    Failed
}

/// <summary>
/// Hole punch status event arguments.
/// </summary>
public class HolePunchStatusEventArgs : EventArgs
{
    public HolePunchStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public IPEndPoint? PublicEndpoint { get; init; }
    public IPEndPoint? RemoteEndpoint { get; init; }
}

/// <summary>
/// Result of a hole punch attempt.
/// </summary>
public class HolePunchResult
{
    public bool Success { get; init; }
    public IPEndPoint? LocalEndpoint { get; init; }
    public IPEndPoint? RemoteEndpoint { get; init; }
    public string? ErrorMessage { get; init; }
}
