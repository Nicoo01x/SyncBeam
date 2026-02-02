using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace SyncBeam.P2P.Discovery;

/// <summary>
/// Scans the local network for all connected devices.
/// </summary>
public sealed class NetworkScanner
{
    private readonly ConcurrentDictionary<string, NetworkDevice> _devices = new();
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;

    public event EventHandler<NetworkDeviceEventArgs>? DeviceDiscovered;
    public event EventHandler? ScanCompleted;

    public IReadOnlyDictionary<string, NetworkDevice> Devices => _devices;

    /// <summary>
    /// Start scanning the local network for devices.
    /// </summary>
    public void StartScan()
    {
        StopScan();
        _scanCts = new CancellationTokenSource();
        _scanTask = ScanNetworkAsync(_scanCts.Token);
    }

    /// <summary>
    /// Stop the current scan.
    /// </summary>
    public void StopScan()
    {
        _scanCts?.Cancel();
        try
        {
            _scanTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private async Task ScanNetworkAsync(CancellationToken ct)
    {
        try
        {
            var localAddresses = GetLocalNetworkInfo();

            foreach (var (localIp, subnetMask) in localAddresses)
            {
                if (ct.IsCancellationRequested) break;

                System.Diagnostics.Debug.WriteLine($"[NetworkScanner] Scanning network: {localIp}/{subnetMask}");
                await ScanSubnetAsync(localIp, subnetMask, ct);
            }

            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetworkScanner] Error: {ex.Message}");
        }
    }

    private async Task ScanSubnetAsync(IPAddress localIp, IPAddress subnetMask, CancellationToken ct)
    {
        var networkAddress = GetNetworkAddress(localIp, subnetMask);
        var broadcastAddress = GetBroadcastAddress(localIp, subnetMask);

        var startIp = IpToUint(networkAddress) + 1;
        var endIp = IpToUint(broadcastAddress) - 1;

        // Limit scan to /24 networks max (254 hosts) to avoid long scans
        var maxHosts = Math.Min(endIp - startIp, 254);

        System.Diagnostics.Debug.WriteLine($"[NetworkScanner] Scanning {maxHosts} addresses from {UintToIp(startIp)} to {UintToIp(startIp + maxHosts)}");

        // Ping in parallel batches
        var batchSize = 50;
        for (uint i = 0; i < maxHosts && !ct.IsCancellationRequested; i += (uint)batchSize)
        {
            var tasks = new List<Task>();
            for (uint j = 0; j < batchSize && (i + j) < maxHosts; j++)
            {
                var ip = UintToIp(startIp + i + j);
                if (!ip.Equals(localIp)) // Skip our own IP
                {
                    tasks.Add(PingAndDiscoverAsync(ip, ct));
                }
            }

            await Task.WhenAll(tasks);
        }
    }

    private async Task PingAndDiscoverAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 500); // 500ms timeout

            if (reply.Status == IPStatus.Success)
            {
                var hostname = await ResolveHostnameAsync(ip);
                var deviceType = DetectDeviceType(ip, hostname);

                var device = new NetworkDevice
                {
                    IpAddress = ip,
                    Hostname = hostname,
                    MacAddress = GetMacAddress(ip),
                    LastSeen = DateTime.Now,
                    HasSyncBeam = false, // Will be updated by mDNS discovery
                    DeviceType = deviceType
                };

                var key = ip.ToString();
                _devices.AddOrUpdate(key, device, (_, existing) =>
                {
                    existing.LastSeen = DateTime.Now;
                    if (string.IsNullOrEmpty(existing.Hostname) && !string.IsNullOrEmpty(device.Hostname))
                        existing.Hostname = device.Hostname;
                    if (existing.DeviceType == DeviceType.Unknown && device.DeviceType != DeviceType.Unknown)
                        existing.DeviceType = device.DeviceType;
                    return existing;
                });

                System.Diagnostics.Debug.WriteLine($"[NetworkScanner] Found device: {ip} ({device.Hostname ?? "unknown"}) - {device.DeviceType}");

                DeviceDiscovered?.Invoke(this, new NetworkDeviceEventArgs { Device = device });
            }
        }
        catch
        {
            // Device didn't respond or error occurred
        }
    }

    private static async Task<string?> ResolveHostnameAsync(IPAddress ip)
    {
        // Try multiple methods to resolve hostname
        string? hostname = null;

        // Method 1: DNS reverse lookup
        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(ip);
            hostname = hostEntry.HostName;

            // Clean up the hostname (remove domain suffix if present)
            if (!string.IsNullOrEmpty(hostname))
            {
                var dotIndex = hostname.IndexOf('.');
                if (dotIndex > 0 && !hostname.StartsWith("192.") && !hostname.StartsWith("10.") && !hostname.StartsWith("172."))
                {
                    hostname = hostname[..dotIndex];
                }
            }
        }
        catch
        {
            // DNS lookup failed
        }

        // Method 2: Try NetBIOS name (Windows)
        if (string.IsNullOrEmpty(hostname) || hostname == ip.ToString())
        {
            try
            {
                hostname = await GetNetBiosNameAsync(ip);
            }
            catch
            {
                // NetBIOS lookup failed
            }
        }

        return hostname;
    }

    private static async Task<string?> GetNetBiosNameAsync(IPAddress ip)
    {
        try
        {
            // Try to connect to NetBIOS name service port 137
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 500;
            udp.Client.SendTimeout = 500;

            // NetBIOS name query packet
            byte[] query = new byte[] {
                0x80, 0x94, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x20, 0x43, 0x4b, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x00, 0x00, 0x21,
                0x00, 0x01
            };

            await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 137));

            var receiveTask = udp.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(500)) == receiveTask)
            {
                var result = await receiveTask;
                if (result.Buffer.Length > 56)
                {
                    // Extract the NetBIOS name from the response
                    var nameBytes = new byte[15];
                    Array.Copy(result.Buffer, 57, nameBytes, 0, Math.Min(15, result.Buffer.Length - 57));
                    var name = System.Text.Encoding.ASCII.GetString(nameBytes).Trim('\0', ' ');
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
        }
        catch
        {
            // NetBIOS query failed
        }
        return null;
    }

    private static DeviceType DetectDeviceType(IPAddress ip, string? hostname)
    {
        var ipStr = ip.ToString();
        var hostLower = hostname?.ToLowerInvariant() ?? "";

        // Check for router (usually .1 or .254)
        var lastOctet = ip.GetAddressBytes()[3];
        if (lastOctet == 1 || lastOctet == 254)
            return DeviceType.Router;

        // Check hostname patterns
        if (hostLower.Contains("iphone") || hostLower.Contains("android") ||
            hostLower.Contains("galaxy") || hostLower.Contains("pixel") ||
            hostLower.Contains("huawei") || hostLower.Contains("xiaomi") ||
            hostLower.Contains("redmi") || hostLower.Contains("oppo") ||
            hostLower.Contains("phone") || hostLower.Contains("movil"))
            return DeviceType.Phone;

        if (hostLower.Contains("ipad") || hostLower.Contains("tablet") ||
            hostLower.Contains("tab-") || hostLower.Contains("surface"))
            return DeviceType.Tablet;

        if (hostLower.Contains("tv") || hostLower.Contains("roku") ||
            hostLower.Contains("chromecast") || hostLower.Contains("firestick") ||
            hostLower.Contains("apple-tv") || hostLower.Contains("smarttv"))
            return DeviceType.SmartTV;

        if (hostLower.Contains("playstation") || hostLower.Contains("xbox") ||
            hostLower.Contains("nintendo") || hostLower.Contains("ps4") ||
            hostLower.Contains("ps5") || hostLower.Contains("switch"))
            return DeviceType.GameConsole;

        if (hostLower.Contains("printer") || hostLower.Contains("hp-") ||
            hostLower.Contains("epson") || hostLower.Contains("canon") ||
            hostLower.Contains("brother"))
            return DeviceType.Printer;

        if (hostLower.Contains("echo") || hostLower.Contains("alexa") ||
            hostLower.Contains("google-home") || hostLower.Contains("nest") ||
            hostLower.Contains("hue") || hostLower.Contains("smartplug"))
            return DeviceType.SmartHome;

        if (hostLower.Contains("server") || hostLower.Contains("nas") ||
            hostLower.Contains("synology") || hostLower.Contains("qnap"))
            return DeviceType.Server;

        if (hostLower.Contains("desktop") || hostLower.Contains("laptop") ||
            hostLower.Contains("pc") || hostLower.Contains("macbook") ||
            hostLower.Contains("imac") || hostLower.Contains("windows") ||
            hostLower.Contains("-pc") || hostLower.EndsWith("pc"))
            return DeviceType.Computer;

        // If hostname exists and doesn't match patterns, assume computer
        if (!string.IsNullOrEmpty(hostname) && hostname != ipStr)
            return DeviceType.Computer;

        return DeviceType.Unknown;
    }

    private static string? GetMacAddress(IPAddress ip)
    {
        // Try to get MAC from ARP cache (Windows only)
        try
        {
            // This is a simplified approach - in production you'd use ARP table
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mark a device as having SyncBeam installed.
    /// </summary>
    public void MarkAsSyncBeamDevice(IPAddress ip, string peerId)
    {
        var key = ip.ToString();
        if (_devices.TryGetValue(key, out var device))
        {
            device.HasSyncBeam = true;
            device.SyncBeamPeerId = peerId;
            DeviceDiscovered?.Invoke(this, new NetworkDeviceEventArgs { Device = device });
        }
        else
        {
            // Device discovered via mDNS before network scan
            var newDevice = new NetworkDevice
            {
                IpAddress = ip,
                HasSyncBeam = true,
                SyncBeamPeerId = peerId,
                LastSeen = DateTime.Now
            };
            _devices.TryAdd(key, newDevice);
            DeviceDiscovered?.Invoke(this, new NetworkDeviceEventArgs { Device = newDevice });
        }
    }

    private static IEnumerable<(IPAddress ip, IPAddress mask)> GetLocalNetworkInfo()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    // Skip virtual/docker networks (usually 172.x.x.x or 10.x.x.x with specific patterns)
                    var bytes = addr.Address.GetAddressBytes();
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        continue; // Docker/WSL networks

                    yield return (addr.Address, addr.IPv4Mask);
                }
            }
        }
    }

    private static IPAddress GetNetworkAddress(IPAddress ip, IPAddress subnetMask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var networkBytes = new byte[4];

        for (int i = 0; i < 4; i++)
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);

        return new IPAddress(networkBytes);
    }

    private static IPAddress GetBroadcastAddress(IPAddress ip, IPAddress subnetMask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var broadcastBytes = new byte[4];

        for (int i = 0; i < 4; i++)
            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

        return new IPAddress(broadcastBytes);
    }

    private static uint IpToUint(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UintToIp(uint ip)
    {
        return new IPAddress(new byte[]
        {
            (byte)(ip >> 24),
            (byte)(ip >> 16),
            (byte)(ip >> 8),
            (byte)ip
        });
    }
}

/// <summary>
/// Represents a device found on the network.
/// </summary>
public class NetworkDevice
{
    public required IPAddress IpAddress { get; init; }
    public string? Hostname { get; set; }
    public string? MacAddress { get; set; }
    public DateTime LastSeen { get; set; }
    public bool HasSyncBeam { get; set; }
    public string? SyncBeamPeerId { get; set; }
    public bool IsConnected { get; set; }
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
}

/// <summary>
/// Type of network device.
/// </summary>
public enum DeviceType
{
    Unknown,
    Computer,
    Phone,
    Tablet,
    Router,
    Printer,
    SmartTV,
    GameConsole,
    SmartHome,
    Server
}

public class NetworkDeviceEventArgs : EventArgs
{
    public required NetworkDevice Device { get; init; }
}
