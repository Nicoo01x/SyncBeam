using System.Diagnostics;
using System.Net;
using SyncBeam.P2P.NatTraversal;

namespace SyncBeam.P2P.Network;

/// <summary>
/// Central coordinator for network setup and configuration.
/// Handles firewall, UPnP, STUN, and provides diagnostics.
/// </summary>
public class NetworkSetupManager : IDisposable
{
    private readonly UpnpManager _upnpManager;
    private readonly NetworkDiagnostics _diagnostics;
    private readonly int _port;
    private bool _disposed;

    private int _tcpMappedPort;
    private int _udpMappedPort;

    /// <summary>
    /// Gets whether the firewall is configured for SyncBeam.
    /// </summary>
    public bool IsFirewallConfigured => FirewallManager.AreRulesConfigured();

    /// <summary>
    /// Gets whether UPnP is available on the network.
    /// </summary>
    public bool IsUPnPAvailable => _upnpManager.IsDeviceDiscovered;

    /// <summary>
    /// Gets whether ports are mapped via UPnP.
    /// </summary>
    public bool IsPortMapped => _tcpMappedPort > 0 || _udpMappedPort > 0;

    /// <summary>
    /// Gets the detected NAT type.
    /// </summary>
    public NatType DetectedNatType { get; private set; } = NatType.Unknown;

    /// <summary>
    /// Gets the public endpoint discovered via STUN.
    /// </summary>
    public IPEndPoint? PublicEndpoint { get; private set; }

    /// <summary>
    /// Gets the external IP address (from UPnP or STUN).
    /// </summary>
    public IPAddress? ExternalIpAddress => _upnpManager.ExternalIpAddress ?? PublicEndpoint?.Address;

    /// <summary>
    /// Gets the local IP address used for the gateway.
    /// </summary>
    public IPAddress? LocalIpAddress => _upnpManager.LocalIpAddress;

    /// <summary>
    /// Gets the gateway/router address.
    /// </summary>
    public IPAddress? GatewayAddress => _upnpManager.GatewayAddress;

    /// <summary>
    /// Event raised when network setup status changes.
    /// </summary>
    public event EventHandler<NetworkStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Event raised to report setup progress.
    /// </summary>
    public event EventHandler<SetupProgressEventArgs>? SetupProgress;

    public NetworkSetupManager(int port)
    {
        _port = port;
        _upnpManager = new UpnpManager();
        _diagnostics = new NetworkDiagnostics(port);

        _upnpManager.StatusChanged += (_, e) =>
        {
            StatusChanged?.Invoke(this, new NetworkStatusEventArgs
            {
                Component = NetworkComponent.UPnP,
                Status = e.Status == UpnpStatus.Ready || e.Status == UpnpStatus.PortMapped
                    ? NetworkSetupStatus.Ready
                    : e.Status == UpnpStatus.Error
                        ? NetworkSetupStatus.Error
                        : NetworkSetupStatus.InProgress,
                Message = e.Message
            });
        };
    }

    /// <summary>
    /// Performs complete network setup: firewall, UPnP, and STUN.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Setup result with detailed status</returns>
    public async Task<NetworkSetupResult> SetupAsync(CancellationToken ct = default)
    {
        var result = new NetworkSetupResult();

        // Step 1: Check and configure firewall
        ReportProgress("Checking firewall configuration...", 0);
        result.FirewallResult = await ConfigureFirewallAsync();

        if (!result.FirewallResult.Success && result.FirewallResult.RequiresElevation)
        {
            ReportProgress("Firewall requires administrator privileges", 10);
        }
        else if (result.FirewallResult.Success)
        {
            ReportProgress("Firewall configured", 20);
        }

        // Step 2: Discover UPnP device
        ReportProgress("Discovering UPnP gateway...", 25);
        result.UpnpDiscovered = await _upnpManager.DiscoverDeviceAsync(TimeSpan.FromSeconds(5), ct);

        if (result.UpnpDiscovered)
        {
            ReportProgress($"UPnP gateway found at {_upnpManager.GatewayAddress}", 40);

            // Step 3: Create port mappings
            ReportProgress("Creating port mappings...", 45);
            result.TcpMappingResult = await ConfigureUpnpAsync(_port, PortMappingProtocol.TCP, ct);
            result.UdpMappingResult = await ConfigureUpnpAsync(_port, PortMappingProtocol.UDP, ct);

            if (result.TcpMappingResult.Success)
            {
                _tcpMappedPort = result.TcpMappingResult.ExternalPort;
                ReportProgress($"TCP port {_port} mapped", 55);
            }

            if (result.UdpMappingResult.Success)
            {
                _udpMappedPort = result.UdpMappingResult.ExternalPort;
                ReportProgress($"UDP port {_port} mapped", 65);
            }
        }
        else
        {
            ReportProgress("UPnP not available", 40);
        }

        // Step 4: Detect NAT and public endpoint using STUN
        ReportProgress("Detecting NAT type...", 70);
        var natInfo = await DetectNatAsync(ct);
        result.NatInfo = natInfo;

        if (natInfo.PublicEndpoint != null)
        {
            PublicEndpoint = natInfo.PublicEndpoint;
            DetectedNatType = natInfo.NatType;
            ReportProgress($"Public endpoint: {PublicEndpoint}, NAT type: {DetectedNatType}", 90);
        }
        else
        {
            ReportProgress("Could not detect public endpoint", 90);
        }

        // Determine overall status
        result.IsReady = result.FirewallResult.Success || IsFirewallConfigured;
        result.CanAcceptExternalConnections = result.IsReady &&
            (IsPortMapped || DetectedNatType == NatType.None || DetectedNatType == NatType.FullCone);

        ReportProgress("Network setup complete", 100);

        // Emit final status
        StatusChanged?.Invoke(this, new NetworkStatusEventArgs
        {
            Component = NetworkComponent.Overall,
            Status = result.IsReady ? NetworkSetupStatus.Ready : NetworkSetupStatus.Warning,
            Message = GetStatusSummary(result)
        });

        return result;
    }

    /// <summary>
    /// Configures Windows Firewall for SyncBeam.
    /// </summary>
    public Task<FirewallConfigResult> ConfigureFirewallAsync()
    {
        var result = FirewallManager.ConfigureRules(_port, _port);

        StatusChanged?.Invoke(this, new NetworkStatusEventArgs
        {
            Component = NetworkComponent.Firewall,
            Status = result.Success ? NetworkSetupStatus.Ready
                : result.RequiresElevation ? NetworkSetupStatus.Warning
                : NetworkSetupStatus.Error,
            Message = result.Message
        });

        return Task.FromResult(result);
    }

    /// <summary>
    /// Configures UPnP port mapping.
    /// </summary>
    public async Task<PortMappingResult> ConfigureUpnpAsync(
        int port,
        PortMappingProtocol protocol = PortMappingProtocol.TCP,
        CancellationToken ct = default)
    {
        if (!_upnpManager.IsDeviceDiscovered)
        {
            var discovered = await _upnpManager.DiscoverDeviceAsync(TimeSpan.FromSeconds(5), ct);
            if (!discovered)
            {
                return new PortMappingResult
                {
                    Success = false,
                    ErrorMessage = "No UPnP gateway found"
                };
            }
        }

        return await _upnpManager.CreatePortMappingAsync(port, port, protocol, 0, ct);
    }

    /// <summary>
    /// Detects NAT type and public endpoint using STUN.
    /// </summary>
    public async Task<NatInfo> DetectNatAsync(CancellationToken ct = default)
    {
        var info = new NatInfo();

        try
        {
            // Get public endpoint from STUN
            var publicEndpoint = await StunClient.DiscoverPublicEndpointAsync(_port, ct);

            if (publicEndpoint != null)
            {
                info.PublicEndpoint = publicEndpoint;

                // Compare with local addresses to determine if behind NAT
                var localAddresses = GetLocalAddresses();

                if (localAddresses.Contains(publicEndpoint.Address))
                {
                    info.NatType = NatType.None;
                    info.IsBehindNat = false;
                }
                else
                {
                    info.IsBehindNat = true;

                    // Determine NAT type by sending to multiple STUN servers
                    // and comparing the mapped endpoints
                    info.NatType = await DetermineNatTypeAsync(ct);
                }

                info.Success = true;
            }
            else
            {
                info.Success = false;
                info.ErrorMessage = "Could not reach STUN servers";
            }
        }
        catch (Exception ex)
        {
            info.Success = false;
            info.ErrorMessage = ex.Message;
        }

        return info;
    }

    /// <summary>
    /// Runs network diagnostics.
    /// </summary>
    public Task<DiagnosticReport> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        return _diagnostics.RunDiagnosticsAsync(ct);
    }

    /// <summary>
    /// Checks connectivity to a specific peer.
    /// </summary>
    public Task<PeerConnectivityResult> CheckPeerConnectivityAsync(
        IPEndPoint endpoint,
        CancellationToken ct = default)
    {
        return _diagnostics.CheckPeerConnectivityAsync(endpoint, ct);
    }

    /// <summary>
    /// Quick check if network is ready.
    /// </summary>
    public Task<QuickCheckResult> QuickCheckAsync(CancellationToken ct = default)
    {
        return _diagnostics.QuickCheckAsync(ct);
    }

    /// <summary>
    /// Removes UPnP port mappings created by this instance.
    /// </summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        try
        {
            if (_tcpMappedPort > 0)
            {
                await _upnpManager.RemovePortMappingAsync(_tcpMappedPort, PortMappingProtocol.TCP, ct);
                _tcpMappedPort = 0;
            }

            if (_udpMappedPort > 0)
            {
                await _upnpManager.RemovePortMappingAsync(_udpMappedPort, PortMappingProtocol.UDP, ct);
                _udpMappedPort = 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkSetupManager] Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets current network status summary.
    /// </summary>
    public NetworkStatusSummary GetStatusSummary()
    {
        return new NetworkStatusSummary
        {
            FirewallConfigured = IsFirewallConfigured,
            UpnpAvailable = IsUPnPAvailable,
            PortMapped = IsPortMapped,
            NatType = DetectedNatType,
            PublicEndpoint = PublicEndpoint,
            ExternalIp = ExternalIpAddress,
            LocalIp = LocalIpAddress,
            GatewayIp = GatewayAddress,
            ListenPort = _port
        };
    }

    private async Task<NatType> DetermineNatTypeAsync(CancellationToken ct)
    {
        try
        {
            // This is a simplified NAT type detection
            // Full RFC 5780 implementation would require more complex testing

            // Create a temporary socket for testing
            using var testSocket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            testSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            var testPort = ((IPEndPoint)testSocket.LocalEndPoint!).Port;

            // Query STUN from the same local port
            var endpoint1 = await StunClient.DiscoverPublicEndpointAsync(testPort, ct);

            await Task.Delay(100, ct);

            var endpoint2 = await StunClient.DiscoverPublicEndpointAsync(testPort, ct);

            if (endpoint1 == null || endpoint2 == null)
                return NatType.Unknown;

            // If mapped ports are the same, likely Full Cone or Restricted
            if (endpoint1.Port == endpoint2.Port && endpoint1.Address.Equals(endpoint2.Address))
            {
                // Could be Full Cone, Restricted Cone, or Port Restricted
                // Without more servers we can't fully distinguish
                return NatType.FullCone;
            }

            // Different ports = Symmetric NAT
            return NatType.Symmetric;
        }
        catch
        {
            return NatType.Unknown;
        }
    }

    private static HashSet<IPAddress> GetLocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();

        try
        {
            var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var address in hostEntry.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    addresses.Add(address);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return addresses;
    }

    private void ReportProgress(string message, int percentComplete)
    {
        Debug.WriteLine($"[NetworkSetupManager] {message} ({percentComplete}%)");
        SetupProgress?.Invoke(this, new SetupProgressEventArgs
        {
            Message = message,
            PercentComplete = percentComplete
        });
    }

    private static string GetStatusSummary(NetworkSetupResult result)
    {
        var parts = new List<string>();

        if (result.FirewallResult.Success)
            parts.Add("Firewall ✓");
        else if (result.FirewallResult.RequiresElevation)
            parts.Add("Firewall (needs admin)");
        else
            parts.Add("Firewall ✗");

        if (result.UpnpDiscovered)
        {
            if (result.TcpMappingResult?.Success == true || result.UdpMappingResult?.Success == true)
                parts.Add("UPnP ✓");
            else
                parts.Add("UPnP (no mapping)");
        }
        else
        {
            parts.Add("UPnP ✗");
        }

        if (result.NatInfo?.Success == true)
        {
            parts.Add($"NAT: {result.NatInfo.NatType}");
        }

        return string.Join(" | ", parts);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // Try to clean up port mappings synchronously on dispose
            try
            {
                CleanupAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore cleanup errors on dispose
            }

            _upnpManager.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Network component types.
/// </summary>
public enum NetworkComponent
{
    Overall,
    Firewall,
    UPnP,
    STUN,
    NAT
}

/// <summary>
/// Network setup status.
/// </summary>
public enum NetworkSetupStatus
{
    NotStarted,
    InProgress,
    Ready,
    Warning,
    Error
}

/// <summary>
/// Network status event arguments.
/// </summary>
public class NetworkStatusEventArgs : EventArgs
{
    public NetworkComponent Component { get; init; }
    public NetworkSetupStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Setup progress event arguments.
/// </summary>
public class SetupProgressEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
    public int PercentComplete { get; init; }
}

/// <summary>
/// Complete result of network setup.
/// </summary>
public class NetworkSetupResult
{
    public bool IsReady { get; set; }
    public bool CanAcceptExternalConnections { get; set; }
    public FirewallConfigResult FirewallResult { get; set; } = new();
    public bool UpnpDiscovered { get; set; }
    public PortMappingResult? TcpMappingResult { get; set; }
    public PortMappingResult? UdpMappingResult { get; set; }
    public NatInfo? NatInfo { get; set; }
}

/// <summary>
/// NAT detection information.
/// </summary>
public class NatInfo
{
    public bool Success { get; set; }
    public bool IsBehindNat { get; set; }
    public NatType NatType { get; set; } = NatType.Unknown;
    public IPEndPoint? PublicEndpoint { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Summary of current network status.
/// </summary>
public class NetworkStatusSummary
{
    public bool FirewallConfigured { get; set; }
    public bool UpnpAvailable { get; set; }
    public bool PortMapped { get; set; }
    public NatType NatType { get; set; }
    public IPEndPoint? PublicEndpoint { get; set; }
    public IPAddress? ExternalIp { get; set; }
    public IPAddress? LocalIp { get; set; }
    public IPAddress? GatewayIp { get; set; }
    public int ListenPort { get; set; }
}
