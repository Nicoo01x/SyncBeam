using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using SyncBeam.P2P.Core;

namespace SyncBeam.P2P.Discovery;

/// <summary>
/// mDNS-based peer discovery for LAN environments.
/// </summary>
public sealed class MdnsDiscovery : IDisposable
{
    private const string ServiceType = "_syncbeam._tcp";
    private const string Domain = "local";

    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly PeerIdentity _localIdentity;
    private readonly int _port;
    private readonly string _instanceName;
    private readonly ServiceProfile _serviceProfile;

    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<DiscoveredPeerEventArgs>? PeerDiscovered;
    public event EventHandler<DiscoveredPeerEventArgs>? PeerLost;

    public MdnsDiscovery(PeerIdentity localIdentity, int port)
    {
        _localIdentity = localIdentity;
        _port = port;

        // Simple instance name with peer ID prefix
        _instanceName = $"syncbeam-{_localIdentity.PeerId[..8]}";

        _mdns = new MulticastService();

        // Ignore virtual network adapters that might interfere
        _mdns.NetworkInterfaceDiscovered += (s, e) =>
        {
            foreach (var nic in e.NetworkInterfaces)
            {
                System.Diagnostics.Debug.WriteLine($"[mDNS] Using network interface: {nic.Name} ({nic.NetworkInterfaceType})");
            }
        };

        _serviceDiscovery = new ServiceDiscovery(_mdns);

        // Create service profile
        _serviceProfile = new ServiceProfile(_instanceName, ServiceType, (ushort)_port);
        _serviceProfile.AddProperty("peerId", _localIdentity.PeerId);
        _serviceProfile.AddProperty("version", "1");

        // Add all local addresses
        foreach (var address in GetLocalAddresses())
        {
            _serviceProfile.Resources.Add(new ARecord
            {
                Name = _serviceProfile.HostName,
                Address = address
            });
        }

        _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _serviceDiscovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;

        // Add debug handlers
        _mdns.QueryReceived += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[mDNS] Query received: {args.Message.Questions.FirstOrDefault()?.Name}");
        };

        _mdns.AnswerReceived += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[mDNS] Answer received from {args.RemoteEndPoint}");
        };
    }

    public void Start()
    {
        if (_isRunning) return;

        System.Diagnostics.Debug.WriteLine($"[mDNS] Starting discovery...");
        System.Diagnostics.Debug.WriteLine($"[mDNS] Instance name: {_instanceName}");
        System.Diagnostics.Debug.WriteLine($"[mDNS] Port: {_port}");
        System.Diagnostics.Debug.WriteLine($"[mDNS] Local addresses: {string.Join(", ", GetLocalAddresses())}");

        _mdns.Start();
        _serviceDiscovery.Advertise(_serviceProfile);
        _serviceDiscovery.QueryServiceInstances(ServiceType);

        _isRunning = true;
        System.Diagnostics.Debug.WriteLine($"[mDNS] Discovery started successfully");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _serviceDiscovery.Unadvertise(_serviceProfile);
        _mdns.Stop();

        _isRunning = false;
    }

    public void QueryPeers()
    {
        if (_isRunning)
        {
            _serviceDiscovery.QueryServiceInstances(ServiceType);
        }
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[mDNS] Service discovered: {e.ServiceInstanceName}");

            // Skip our own instance
            if (e.ServiceInstanceName.Labels[0] == _instanceName)
            {
                System.Diagnostics.Debug.WriteLine($"[mDNS] Skipping own instance");
                return;
            }

            // Check if this is a SyncBeam service
            if (!e.ServiceInstanceName.ToString().Contains(ServiceType))
            {
                System.Diagnostics.Debug.WriteLine($"[mDNS] Not a SyncBeam service, skipping");
                return;
            }

            // Get service details from both Answers and AdditionalRecords
            var peerId = GetTxtProperty(e.Message, "peerId");

            System.Diagnostics.Debug.WriteLine($"[mDNS] PeerId: {peerId}");

            if (string.IsNullOrEmpty(peerId))
            {
                System.Diagnostics.Debug.WriteLine($"[mDNS] No peerId found, skipping");
                return;
            }

            // Get endpoint - check both Answers and AdditionalRecords for SRV
            var srvRecord = e.Message.Answers
                .OfType<SRVRecord>()
                .FirstOrDefault()
                ?? e.Message.AdditionalRecords
                .OfType<SRVRecord>()
                .FirstOrDefault();

            // Get A records from both sections
            var aRecords = e.Message.Answers
                .OfType<ARecord>()
                .Concat(e.Message.AdditionalRecords.OfType<ARecord>())
                .ToList();

            if (srvRecord == null || aRecords.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[mDNS] No SRV or A records found, querying for more details...");
                // Query specifically for this service instance to get full details
                _mdns.SendQuery(e.ServiceInstanceName, type: DnsType.ANY);
                return;
            }

            var endpoint = new IPEndPoint(aRecords[0].Address, srvRecord.Port);
            System.Diagnostics.Debug.WriteLine($"[mDNS] Peer endpoint: {endpoint}");

            PeerDiscovered?.Invoke(this, new DiscoveredPeerEventArgs
            {
                PeerId = peerId,
                Endpoint = endpoint,
                InstanceName = e.ServiceInstanceName.Labels[0]
            });

            System.Diagnostics.Debug.WriteLine($"[mDNS] Peer discovered event raised for {peerId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[mDNS] Error processing discovery: {ex.Message}");
        }
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        try
        {
            var instanceName = e.ServiceInstanceName.Labels[0];
            if (instanceName == _instanceName)
                return;

            // Extract peer ID from instance name if possible
            var parts = instanceName.Split('-');
            if (parts.Length >= 2 && parts[0] == "syncbeam")
            {
                PeerLost?.Invoke(this, new DiscoveredPeerEventArgs
                {
                    PeerId = parts[1],
                    InstanceName = instanceName,
                    Endpoint = null!
                });
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static string? GetTxtProperty(Message message, string key)
    {
        foreach (var record in message.Answers.Concat(message.AdditionalRecords))
        {
            if (record is TXTRecord txt)
            {
                foreach (var str in txt.Strings)
                {
                    var parts = str.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == key)
                        return parts[1];
                }
            }
        }
        return null;
    }

    private static IEnumerable<IPAddress> GetLocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return addr.Address;
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _serviceDiscovery.Dispose();
            _mdns.Dispose();
            _disposed = true;
        }
    }
}

public class DiscoveredPeerEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required IPEndPoint Endpoint { get; init; }
    public required string InstanceName { get; init; }
}
