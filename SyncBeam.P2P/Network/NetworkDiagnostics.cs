using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SyncBeam.P2P.NatTraversal;

namespace SyncBeam.P2P.Network;

/// <summary>
/// Provides network connectivity diagnostics and troubleshooting.
/// </summary>
public class NetworkDiagnostics
{
    private readonly int _listenPort;

    public NetworkDiagnostics(int listenPort)
    {
        _listenPort = listenPort;
    }

    /// <summary>
    /// Runs a comprehensive network diagnostic.
    /// </summary>
    public async Task<DiagnosticReport> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var report = new DiagnosticReport
        {
            StartTime = DateTime.UtcNow,
            ListenPort = _listenPort
        };

        // Check local network interfaces
        report.LocalInterfaces = GetLocalNetworkInterfaces();

        // Check if port is available
        report.PortAvailable = IsPortAvailable(_listenPort);

        // Check internet connectivity
        report.InternetConnectivity = await CheckInternetConnectivityAsync(ct);

        // Check STUN (NAT type detection)
        report.StunResult = await CheckStunAsync(ct);

        // Check UPnP availability
        report.UpnpResult = await CheckUpnpAsync(ct);

        // Check firewall status
        report.FirewallStatus = FirewallManager.GetStatus();

        // Generate recommendations
        report.Recommendations = GenerateRecommendations(report);

        report.EndTime = DateTime.UtcNow;
        report.Duration = report.EndTime - report.StartTime;

        return report;
    }

    /// <summary>
    /// Checks connectivity to a specific peer endpoint.
    /// </summary>
    public async Task<PeerConnectivityResult> CheckPeerConnectivityAsync(
        IPEndPoint endpoint,
        CancellationToken ct = default)
    {
        var result = new PeerConnectivityResult
        {
            Endpoint = endpoint
        };

        // Ping test
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(endpoint.Address, 3000);
            result.PingSuccessful = reply.Status == IPStatus.Success;
            result.PingLatency = result.PingSuccessful ? (int)reply.RoundtripTime : -1;
        }
        catch
        {
            result.PingSuccessful = false;
            result.PingLatency = -1;
        }

        // TCP port test
        result.TcpPortOpen = await CheckTcpPortAsync(endpoint, ct);

        // UDP test (simple reachability)
        result.UdpReachable = await CheckUdpReachabilityAsync(endpoint, ct);

        // Generate diagnosis
        result.Diagnosis = DiagnoseConnectivityIssue(result);

        return result;
    }

    /// <summary>
    /// Quick check if the network is ready for P2P connections.
    /// </summary>
    public async Task<QuickCheckResult> QuickCheckAsync(CancellationToken ct = default)
    {
        var result = new QuickCheckResult();

        // Check network interface
        var interfaces = GetLocalNetworkInterfaces();
        result.HasNetworkInterface = interfaces.Any(i => i.IsUp && !i.IsLoopback);

        // Check port availability
        result.PortAvailable = IsPortAvailable(_listenPort);

        // Check firewall (non-blocking quick check)
        var firewallStatus = FirewallManager.GetStatus();
        result.FirewallConfigured = firewallStatus.RulesConfigured;

        // Quick internet check
        result.HasInternet = await QuickInternetCheckAsync(ct);

        result.IsReady = result.HasNetworkInterface &&
                         result.PortAvailable &&
                         result.FirewallConfigured;

        if (!result.IsReady)
        {
            if (!result.HasNetworkInterface)
                result.Issue = "No network connection detected";
            else if (!result.PortAvailable)
                result.Issue = $"Port {_listenPort} is already in use";
            else if (!result.FirewallConfigured)
                result.Issue = "Firewall rules not configured";
        }

        return result;
    }

    private List<NetworkInterfaceInfo> GetLocalNetworkInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                var ipv4Addresses = ipProps.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .ToList();

                if (ipv4Addresses.Any())
                {
                    interfaces.Add(new NetworkInterfaceInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        Type = ni.NetworkInterfaceType.ToString(),
                        IsUp = ni.OperationalStatus == OperationalStatus.Up,
                        IsLoopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        IPv4Addresses = ipv4Addresses,
                        GatewayAddresses = ipProps.GatewayAddresses
                            .Select(g => g.Address)
                            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                            .ToList()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkDiagnostics] Error getting interfaces: {ex.Message}");
        }

        return interfaces;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<InternetConnectivityResult> CheckInternetConnectivityAsync(CancellationToken ct)
    {
        var result = new InternetConnectivityResult();

        // Try to ping well-known hosts
        var hosts = new[] { "8.8.8.8", "1.1.1.1", "208.67.222.222" };

        foreach (var host in hosts)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                if (reply.Status == IPStatus.Success)
                {
                    result.IsConnected = true;
                    result.Latency = (int)reply.RoundtripTime;
                    result.ReachableHost = host;
                    break;
                }
            }
            catch
            {
                // Try next host
            }
        }

        // Try DNS resolution
        if (result.IsConnected)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync("www.google.com", ct);
                result.DnsWorking = addresses.Length > 0;
            }
            catch
            {
                result.DnsWorking = false;
            }
        }

        return result;
    }

    private async Task<StunResult> CheckStunAsync(CancellationToken ct)
    {
        var result = new StunResult();

        try
        {
            // Try to discover public endpoint using STUN
            var publicEndpoint = await StunClient.DiscoverPublicEndpointAsync(_listenPort, ct);

            if (publicEndpoint != null)
            {
                result.Success = true;
                result.PublicEndpoint = publicEndpoint;

                // Detect NAT type by comparing with local addresses
                var localInterfaces = GetLocalNetworkInterfaces();
                var localAddresses = localInterfaces
                    .Where(i => i.IsUp)
                    .SelectMany(i => i.IPv4Addresses)
                    .ToList();

                if (localAddresses.Contains(publicEndpoint.Address))
                {
                    result.NatType = NatType.None;
                }
                else
                {
                    // We'd need multiple STUN queries to different servers to fully determine NAT type
                    // For now, we'll do a basic check
                    result.NatType = await DetectNatTypeAsync(ct);
                }
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = "Could not reach STUN servers";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<NatType> DetectNatTypeAsync(CancellationToken ct)
    {
        // Perform NAT type detection using multiple STUN queries
        // This is a simplified implementation
        try
        {
            // Query different STUN servers from the same local port
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;

            var endpoint1 = await StunClient.DiscoverPublicEndpointAsync(localPort, ct);

            // Small delay before second query
            await Task.Delay(100, ct);

            var endpoint2 = await StunClient.DiscoverPublicEndpointAsync(localPort, ct);

            if (endpoint1 == null || endpoint2 == null)
                return NatType.Unknown;

            // If endpoints match, likely Full Cone or Restricted Cone
            if (endpoint1.Port == endpoint2.Port)
                return NatType.FullCone;

            // If ports differ, likely Symmetric NAT
            return NatType.Symmetric;
        }
        catch
        {
            return NatType.Unknown;
        }
    }

    private static async Task<UpnpCheckResult> CheckUpnpAsync(CancellationToken ct)
    {
        var result = new UpnpCheckResult();

        try
        {
            using var upnp = new UpnpManager();
            result.DeviceFound = await upnp.DiscoverDeviceAsync(TimeSpan.FromSeconds(5), ct);

            if (result.DeviceFound)
            {
                result.GatewayAddress = upnp.GatewayAddress;
                result.ExternalIpAddress = upnp.ExternalIpAddress;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static async Task<bool> CheckTcpPortAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(endpoint.Address, endpoint.Port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckUdpReachabilityAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            var testData = new byte[] { 0x50, 0x49, 0x4E, 0x47 }; // "PING"

            await client.SendAsync(testData, endpoint, ct);

            // We can't really know if UDP is reachable without a response
            // Just return true if we could send
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> QuickInternetCheckAsync(CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static string DiagnoseConnectivityIssue(PeerConnectivityResult result)
    {
        if (!result.PingSuccessful)
        {
            return $"Cannot reach {result.Endpoint.Address}. " +
                   "The device may be offline, on a different network, or blocking ICMP.";
        }

        if (!result.TcpPortOpen)
        {
            return $"Device is reachable but port {result.Endpoint.Port} is closed. " +
                   "Check if SyncBeam is running on the remote device and firewall rules are configured.";
        }

        return "Connection should work. If issues persist, try restarting SyncBeam on both devices.";
    }

    private List<string> GenerateRecommendations(DiagnosticReport report)
    {
        var recommendations = new List<string>();

        // Check firewall
        if (!report.FirewallStatus.RulesConfigured)
        {
            if (report.FirewallStatus.IsAdmin)
            {
                recommendations.Add("Configure Windows Firewall rules for SyncBeam (you have admin rights).");
            }
            else
            {
                recommendations.Add("Run SyncBeam as Administrator to configure firewall rules, or manually run add-firewall-rules.bat.");
            }
        }

        // Check port
        if (!report.PortAvailable)
        {
            recommendations.Add($"Port {_listenPort} is already in use. Close other applications using this port or restart SyncBeam.");
        }

        // Check NAT
        if (report.StunResult.NatType == NatType.Symmetric)
        {
            recommendations.Add("Your NAT type is Symmetric, which may prevent direct P2P connections. Consider using UPnP or port forwarding.");
        }

        // Check UPnP
        if (!report.UpnpResult.DeviceFound)
        {
            recommendations.Add("UPnP is not available on your router. You may need to manually configure port forwarding for connections outside your local network.");
        }
        else if (report.UpnpResult.ExternalIpAddress == null)
        {
            recommendations.Add("UPnP device found but external IP could not be determined. Your router may have UPnP partially disabled.");
        }

        // Check internet
        if (!report.InternetConnectivity.IsConnected)
        {
            recommendations.Add("No internet connection detected. P2P will only work within local network.");
        }
        else if (!report.InternetConnectivity.DnsWorking)
        {
            recommendations.Add("DNS resolution is not working. This may affect discovery of peers outside local network.");
        }

        // Check interfaces
        var activeInterfaces = report.LocalInterfaces.Where(i => i.IsUp && !i.IsLoopback).ToList();
        if (activeInterfaces.Count == 0)
        {
            recommendations.Add("No active network interfaces found. Check your network connection.");
        }
        else if (activeInterfaces.Count > 1)
        {
            recommendations.Add($"Multiple network interfaces detected ({activeInterfaces.Count}). SyncBeam will listen on all interfaces.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("Network configuration looks good! If you're having issues, ensure SyncBeam is running on both devices.");
        }

        return recommendations;
    }
}

/// <summary>
/// Comprehensive diagnostic report.
/// </summary>
public class DiagnosticReport
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public int ListenPort { get; set; }
    public bool PortAvailable { get; set; }
    public List<NetworkInterfaceInfo> LocalInterfaces { get; set; } = new();
    public InternetConnectivityResult InternetConnectivity { get; set; } = new();
    public StunResult StunResult { get; set; } = new();
    public UpnpCheckResult UpnpResult { get; set; } = new();
    public FirewallStatus FirewallStatus { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Information about a network interface.
/// </summary>
public class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public bool IsLoopback { get; set; }
    public List<IPAddress> IPv4Addresses { get; set; } = new();
    public List<IPAddress> GatewayAddresses { get; set; } = new();
}

/// <summary>
/// Internet connectivity check result.
/// </summary>
public class InternetConnectivityResult
{
    public bool IsConnected { get; set; }
    public int Latency { get; set; }
    public string? ReachableHost { get; set; }
    public bool DnsWorking { get; set; }
}

/// <summary>
/// STUN check result.
/// </summary>
public class StunResult
{
    public bool Success { get; set; }
    public IPEndPoint? PublicEndpoint { get; set; }
    public NatType NatType { get; set; } = NatType.Unknown;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// UPnP check result.
/// </summary>
public class UpnpCheckResult
{
    public bool DeviceFound { get; set; }
    public IPAddress? GatewayAddress { get; set; }
    public IPAddress? ExternalIpAddress { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of checking connectivity to a specific peer.
/// </summary>
public class PeerConnectivityResult
{
    public IPEndPoint Endpoint { get; set; } = null!;
    public bool PingSuccessful { get; set; }
    public int PingLatency { get; set; }
    public bool TcpPortOpen { get; set; }
    public bool UdpReachable { get; set; }
    public string Diagnosis { get; set; } = string.Empty;
}

/// <summary>
/// Quick network check result.
/// </summary>
public class QuickCheckResult
{
    public bool IsReady { get; set; }
    public bool HasNetworkInterface { get; set; }
    public bool PortAvailable { get; set; }
    public bool FirewallConfigured { get; set; }
    public bool HasInternet { get; set; }
    public string? Issue { get; set; }
}

/// <summary>
/// NAT type classification.
/// </summary>
public enum NatType
{
    Unknown,
    None,           // No NAT (public IP)
    FullCone,       // Full Cone NAT (easiest for P2P)
    RestrictedCone, // Restricted Cone NAT
    PortRestricted, // Port Restricted Cone NAT
    Symmetric       // Symmetric NAT (hardest for P2P)
}
