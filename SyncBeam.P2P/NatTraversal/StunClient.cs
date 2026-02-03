using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace SyncBeam.P2P.NatTraversal;

/// <summary>
/// Simple STUN client for discovering public IP and port.
/// Implements RFC 5389 binding request/response.
/// </summary>
public static class StunClient
{
    private static readonly string[] PublicStunServers =
    [
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun2.l.google.com:19302",
        "stun3.l.google.com:19302",
        "stun4.l.google.com:19302",
        "stun.cloudflare.com:3478",
        "stun.stunprotocol.org:3478",
        "stun.voip.blackberry.com:3478",
        "stun.nextcloud.com:443"
    ];

    private const int StunHeaderSize = 20;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const uint MagicCookie = 0x2112A442;

    /// <summary>
    /// Discovers the public endpoint by querying STUN servers.
    /// </summary>
    public static async Task<IPEndPoint?> DiscoverPublicEndpointAsync(
        int localPort,
        CancellationToken ct = default)
    {
        // Try servers in parallel for faster discovery, take first success
        var tasks = PublicStunServers.Select(server => Task.Run(async () =>
        {
            try
            {
                var parts = server.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);

                return await QueryStunServerAsync(host, port, localPort, ct);
            }
            catch
            {
                return null;
            }
        }, ct)).ToList();

        // Wait for first successful result
        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);

            try
            {
                var result = await completedTask;
                if (result != null)
                    return result;
            }
            catch
            {
                // Continue with other tasks
            }
        }

        return null;
    }

    /// <summary>
    /// Discovers the public endpoint by querying a specific STUN server.
    /// </summary>
    public static async Task<IPEndPoint?> DiscoverPublicEndpointFromServerAsync(
        string stunServer,
        int localPort,
        CancellationToken ct = default)
    {
        try
        {
            var parts = stunServer.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

            return await QueryStunServerAsync(host, port, localPort, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the list of available STUN servers.
    /// </summary>
    public static IReadOnlyList<string> GetStunServers() => PublicStunServers;

    private static async Task<IPEndPoint?> QueryStunServerAsync(
        string host,
        int port,
        int localPort,
        CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var serverEndpoint = new IPEndPoint(addresses[0], port);

        // Create STUN binding request
        var request = CreateBindingRequest();

        // Send request
        await socket.SendToAsync(request, SocketFlags.None, serverEndpoint, ct);

        // Receive response with timeout
        var buffer = new byte[1024];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                serverEndpoint,
                timeoutCts.Token);

            return ParseBindingResponse(buffer.AsSpan(0, result.ReceivedBytes));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[] CreateBindingRequest()
    {
        var request = new byte[StunHeaderSize];

        // Message Type: Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), BindingRequest);

        // Message Length: 0 (no attributes)
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);

        // Magic Cookie
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);

        // Transaction ID (96 bits random)
        Random.Shared.NextBytes(request.AsSpan(8, 12));

        return request;
    }

    private static IPEndPoint? ParseBindingResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < StunHeaderSize)
            return null;

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(response);
        if (messageType != BindingResponse)
            return null;

        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(response[4..]);

        if (magicCookie != MagicCookie)
            return null;

        // Parse attributes
        var offset = StunHeaderSize;
        while (offset + 4 <= response.Length)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);

            offset += 4;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
            if (attrType == 0x0020 || attrType == 0x0001)
            {
                if (offset + attrLength > response.Length)
                    return null;

                var attrData = response.Slice(offset, attrLength);
                return ParseMappedAddress(attrData, attrType == 0x0020, response);
            }

            // Align to 4-byte boundary
            offset += (attrLength + 3) & ~3;
        }

        return null;
    }

    private static IPEndPoint? ParseMappedAddress(
        ReadOnlySpan<byte> data,
        bool isXor,
        ReadOnlySpan<byte> response)
    {
        if (data.Length < 8)
            return null;

        var family = data[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var address = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

        if (isXor)
        {
            // XOR with magic cookie and transaction ID
            port ^= (ushort)(MagicCookie >> 16);
            address ^= MagicCookie;
        }

        if (family != 0x01) // IPv4
            return null;

        var ip = new IPAddress(BinaryPrimitives.ReverseEndianness(address));
        return new IPEndPoint(ip, port);
    }
}

/// <summary>
/// UDP hole punching coordinator.
/// </summary>
public class HolePuncher : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly int _localPort;
    private bool _disposed;

    public IPEndPoint? PublicEndpoint { get; private set; }
    public int LocalPort => _localPort;

    public HolePuncher(int localPort = 0)
    {
        _udpClient = new UdpClient(localPort);
        _localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Discovers public endpoint using STUN.
    /// </summary>
    public async Task<bool> DiscoverPublicEndpointAsync(CancellationToken ct = default)
    {
        PublicEndpoint = await StunClient.DiscoverPublicEndpointAsync(_localPort, ct);
        return PublicEndpoint != null;
    }

    /// <summary>
    /// Performs UDP hole punching to establish connectivity with a remote peer.
    /// Both peers should call this simultaneously with each other's public endpoint.
    /// </summary>
    public async Task<bool> PunchHoleAsync(
        IPEndPoint remotePublicEndpoint,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var punchPacket = new byte[] { 0x50, 0x55, 0x4E, 0x43, 0x48 }; // "PUNCH"
        var startTime = DateTime.UtcNow;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // Send punch packets periodically
            var sendTask = Task.Run(async () =>
            {
                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    await _udpClient.SendAsync(punchPacket, remotePublicEndpoint, timeoutCts.Token);
                    await Task.Delay(100, timeoutCts.Token);
                }
            }, timeoutCts.Token);

            // Wait for response
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(timeoutCts.Token);

                if (result.Buffer.Length == 5 &&
                    result.Buffer[0] == 0x50 &&
                    result.Buffer[1] == 0x55)
                {
                    // Received punch packet from remote
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancelled
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _udpClient.Dispose();
            _disposed = true;
        }
    }
}
