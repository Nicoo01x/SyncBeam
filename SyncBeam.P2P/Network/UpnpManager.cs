using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SyncBeam.P2P.Network;

/// <summary>
/// Manages UPnP/NAT-PMP port mappings for automatic router configuration.
/// Implements UPnP IGD (Internet Gateway Device) protocol.
/// </summary>
public class UpnpManager : IDisposable
{
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
    private const string MappingDescription = "SyncBeam P2P";

    private string? _controlUrl;
    private string? _serviceType;
    private IPAddress? _gatewayAddress;
    private bool _disposed;

    /// <summary>
    /// Gets whether a UPnP-enabled device was discovered.
    /// </summary>
    public bool IsDeviceDiscovered => _controlUrl != null;

    /// <summary>
    /// Gets the external/public IP address of the gateway.
    /// </summary>
    public IPAddress? ExternalIpAddress { get; private set; }

    /// <summary>
    /// Gets the local IP address used to communicate with the gateway.
    /// </summary>
    public IPAddress? LocalIpAddress { get; private set; }

    /// <summary>
    /// Gets the gateway/router address.
    /// </summary>
    public IPAddress? GatewayAddress => _gatewayAddress;

    /// <summary>
    /// Event raised when discovery status changes.
    /// </summary>
    public event EventHandler<UpnpStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Discovers UPnP-enabled gateway devices on the network.
    /// </summary>
    /// <param name="timeout">Discovery timeout</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if a gateway was discovered</returns>
    public async Task<bool> DiscoverDeviceAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(5);

        try
        {
            StatusChanged?.Invoke(this, new UpnpStatusEventArgs
            {
                Status = UpnpStatus.Discovering,
                Message = "Searching for UPnP gateway..."
            });

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, (int)timeout.Value.TotalMilliseconds);

            var multicastEndpoint = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);

            // Send M-SEARCH request
            var searchRequest = BuildSsdpSearchRequest();
            await socket.SendToAsync(Encoding.ASCII.GetBytes(searchRequest), SocketFlags.None, multicastEndpoint, ct);

            // Receive responses
            var buffer = new byte[4096];
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout.Value);

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token);

                    var response = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);

                    if (response.Contains("InternetGatewayDevice") || response.Contains("WANIPConnection"))
                    {
                        var locationUrl = ExtractHeader(response, "LOCATION");
                        if (!string.IsNullOrEmpty(locationUrl))
                        {
                            _gatewayAddress = ((IPEndPoint)result.RemoteEndPoint).Address;
                            LocalIpAddress = GetLocalAddressForGateway(_gatewayAddress);

                            if (await ParseDeviceDescriptionAsync(locationUrl, ct))
                            {
                                // Try to get external IP
                                ExternalIpAddress = await GetExternalIpAddressAsync(ct);

                                StatusChanged?.Invoke(this, new UpnpStatusEventArgs
                                {
                                    Status = UpnpStatus.Ready,
                                    Message = $"UPnP gateway discovered at {_gatewayAddress}"
                                });

                                return true;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
            }

            StatusChanged?.Invoke(this, new UpnpStatusEventArgs
            {
                Status = UpnpStatus.NotAvailable,
                Message = "No UPnP gateway found on network"
            });

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpnpManager] Discovery failed: {ex.Message}");
            StatusChanged?.Invoke(this, new UpnpStatusEventArgs
            {
                Status = UpnpStatus.Error,
                Message = $"UPnP discovery failed: {ex.Message}"
            });
            return false;
        }
    }

    /// <summary>
    /// Creates a port mapping on the router.
    /// </summary>
    /// <param name="externalPort">External port number</param>
    /// <param name="internalPort">Internal port number</param>
    /// <param name="protocol">Protocol (TCP or UDP)</param>
    /// <param name="leaseDuration">Lease duration in seconds (0 for permanent)</param>
    /// <returns>True if mapping was created successfully</returns>
    public async Task<PortMappingResult> CreatePortMappingAsync(
        int externalPort,
        int internalPort,
        PortMappingProtocol protocol = PortMappingProtocol.TCP,
        int leaseDuration = 0,
        CancellationToken ct = default)
    {
        if (_controlUrl == null || _serviceType == null || LocalIpAddress == null)
        {
            return new PortMappingResult
            {
                Success = false,
                ErrorMessage = "UPnP device not discovered. Call DiscoverDeviceAsync first."
            };
        }

        try
        {
            var protocolStr = protocol == PortMappingProtocol.TCP ? "TCP" : "UDP";

            var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        <u:AddPortMapping xmlns:u=""{_serviceType}"">
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{externalPort}</NewExternalPort>
            <NewProtocol>{protocolStr}</NewProtocol>
            <NewInternalPort>{internalPort}</NewInternalPort>
            <NewInternalClient>{LocalIpAddress}</NewInternalClient>
            <NewEnabled>1</NewEnabled>
            <NewPortMappingDescription>{MappingDescription}</NewPortMappingDescription>
            <NewLeaseDuration>{leaseDuration}</NewLeaseDuration>
        </u:AddPortMapping>
    </s:Body>
</s:Envelope>";

            var response = await SendSoapRequestAsync(_controlUrl, _serviceType, "AddPortMapping", soapBody, ct);

            if (response.Contains("errorCode"))
            {
                var errorCode = ExtractXmlValue(response, "errorCode");
                var errorDesc = ExtractXmlValue(response, "errorDescription");

                // Error 718 = ConflictInMappingEntry (port already mapped)
                // Try to delete existing mapping and retry
                if (errorCode == "718")
                {
                    await RemovePortMappingAsync(externalPort, protocol, ct);
                    return await CreatePortMappingAsync(externalPort, internalPort, protocol, leaseDuration, ct);
                }

                return new PortMappingResult
                {
                    Success = false,
                    ErrorCode = int.TryParse(errorCode, out var code) ? code : -1,
                    ErrorMessage = errorDesc ?? $"UPnP error {errorCode}"
                };
            }

            StatusChanged?.Invoke(this, new UpnpStatusEventArgs
            {
                Status = UpnpStatus.PortMapped,
                Message = $"Port {externalPort} ({protocolStr}) mapped successfully"
            });

            return new PortMappingResult
            {
                Success = true,
                ExternalPort = externalPort,
                InternalPort = internalPort,
                Protocol = protocol
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpnpManager] Port mapping failed: {ex.Message}");
            return new PortMappingResult
            {
                Success = false,
                ErrorMessage = $"Failed to create port mapping: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Removes a port mapping from the router.
    /// </summary>
    public async Task<bool> RemovePortMappingAsync(
        int externalPort,
        PortMappingProtocol protocol = PortMappingProtocol.TCP,
        CancellationToken ct = default)
    {
        if (_controlUrl == null || _serviceType == null)
            return false;

        try
        {
            var protocolStr = protocol == PortMappingProtocol.TCP ? "TCP" : "UDP";

            var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        <u:DeletePortMapping xmlns:u=""{_serviceType}"">
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{externalPort}</NewExternalPort>
            <NewProtocol>{protocolStr}</NewProtocol>
        </u:DeletePortMapping>
    </s:Body>
</s:Envelope>";

            await SendSoapRequestAsync(_controlUrl, _serviceType, "DeletePortMapping", soapBody, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the external IP address from the router.
    /// </summary>
    private async Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        if (_controlUrl == null || _serviceType == null)
            return null;

        try
        {
            var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        <u:GetExternalIPAddress xmlns:u=""{_serviceType}"">
        </u:GetExternalIPAddress>
    </s:Body>
</s:Envelope>";

            var response = await SendSoapRequestAsync(_controlUrl, _serviceType, "GetExternalIPAddress", soapBody, ct);
            var ipStr = ExtractXmlValue(response, "NewExternalIPAddress");

            if (!string.IsNullOrEmpty(ipStr) && IPAddress.TryParse(ipStr, out var ip))
                return ip;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpnpManager] Failed to get external IP: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Gets existing port mappings from the router.
    /// </summary>
    public async Task<List<PortMapping>> GetPortMappingsAsync(CancellationToken ct = default)
    {
        var mappings = new List<PortMapping>();

        if (_controlUrl == null || _serviceType == null)
            return mappings;

        try
        {
            for (int index = 0; index < 100; index++)
            {
                var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        <u:GetGenericPortMappingEntry xmlns:u=""{_serviceType}"">
            <NewPortMappingIndex>{index}</NewPortMappingIndex>
        </u:GetGenericPortMappingEntry>
    </s:Body>
</s:Envelope>";

                var response = await SendSoapRequestAsync(_controlUrl, _serviceType, "GetGenericPortMappingEntry", soapBody, ct);

                if (response.Contains("errorCode"))
                    break;

                var mapping = new PortMapping
                {
                    ExternalPort = int.TryParse(ExtractXmlValue(response, "NewExternalPort"), out var ext) ? ext : 0,
                    InternalPort = int.TryParse(ExtractXmlValue(response, "NewInternalPort"), out var intern) ? intern : 0,
                    InternalClient = ExtractXmlValue(response, "NewInternalClient") ?? "",
                    Protocol = ExtractXmlValue(response, "NewProtocol") == "UDP" ? PortMappingProtocol.UDP : PortMappingProtocol.TCP,
                    Description = ExtractXmlValue(response, "NewPortMappingDescription") ?? "",
                    Enabled = ExtractXmlValue(response, "NewEnabled") == "1"
                };

                mappings.Add(mapping);
            }
        }
        catch
        {
            // End of mappings or error
        }

        return mappings;
    }

    private string BuildSsdpSearchRequest()
    {
        return $"M-SEARCH * HTTP/1.1\r\n" +
               $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
               $"MAN: \"ssdp:discover\"\r\n" +
               $"MX: 3\r\n" +
               $"ST: {SearchTarget}\r\n" +
               $"\r\n";
    }

    private async Task<bool> ParseDeviceDescriptionAsync(string url, CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await httpClient.GetStringAsync(url, ct);

            var doc = XDocument.Parse(response);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            // Find WANIPConnection or WANPPPConnection service
            var services = doc.Descendants(ns + "service");

            foreach (var service in services)
            {
                var serviceType = service.Element(ns + "serviceType")?.Value;
                if (serviceType != null &&
                    (serviceType.Contains("WANIPConnection") || serviceType.Contains("WANPPPConnection")))
                {
                    var controlUrl = service.Element(ns + "controlURL")?.Value;
                    if (!string.IsNullOrEmpty(controlUrl))
                    {
                        // Make control URL absolute
                        var baseUri = new Uri(url);
                        _controlUrl = controlUrl.StartsWith("http")
                            ? controlUrl
                            : new Uri(baseUri, controlUrl).ToString();
                        _serviceType = serviceType;

                        Debug.WriteLine($"[UpnpManager] Found service: {serviceType}");
                        Debug.WriteLine($"[UpnpManager] Control URL: {_controlUrl}");

                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpnpManager] Failed to parse device description: {ex.Message}");
        }

        return false;
    }

    private async Task<string> SendSoapRequestAsync(
        string controlUrl,
        string serviceType,
        string action,
        string body,
        CancellationToken ct)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var content = new StringContent(body, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

        var response = await httpClient.PostAsync(controlUrl, content, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string? ExtractHeader(string response, string headerName)
    {
        var lines = response.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                    return line[(colonIndex + 1)..].Trim();
            }
        }
        return null;
    }

    private static string? ExtractXmlValue(string xml, string elementName)
    {
        var startTag = $"<{elementName}>";
        var endTag = $"</{elementName}>";

        var startIndex = xml.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) return null;

        startIndex += startTag.Length;
        var endIndex = xml.IndexOf(endTag, startIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0) return null;

        return xml[startIndex..endIndex];
    }

    private static IPAddress? GetLocalAddressForGateway(IPAddress gatewayAddress)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(gatewayAddress, 1);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// UPnP discovery and mapping status.
/// </summary>
public enum UpnpStatus
{
    NotStarted,
    Discovering,
    NotAvailable,
    Ready,
    PortMapped,
    Error
}

/// <summary>
/// Port mapping protocol.
/// </summary>
public enum PortMappingProtocol
{
    TCP,
    UDP
}

/// <summary>
/// UPnP status event arguments.
/// </summary>
public class UpnpStatusEventArgs : EventArgs
{
    public UpnpStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Result of a port mapping operation.
/// </summary>
public class PortMappingResult
{
    public bool Success { get; init; }
    public int ExternalPort { get; init; }
    public int InternalPort { get; init; }
    public PortMappingProtocol Protocol { get; init; }
    public int ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Represents a port mapping entry.
/// </summary>
public class PortMapping
{
    public int ExternalPort { get; init; }
    public int InternalPort { get; init; }
    public string InternalClient { get; init; } = string.Empty;
    public PortMappingProtocol Protocol { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool Enabled { get; init; }
}
