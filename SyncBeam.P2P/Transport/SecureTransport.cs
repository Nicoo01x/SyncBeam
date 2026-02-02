using System.Buffers.Binary;
using System.Net.Sockets;
using SyncBeam.P2P.Core;
using SyncBeam.P2P.Handshake;

namespace SyncBeam.P2P.Transport;

/// <summary>
/// Secure transport layer providing encrypted communication over TCP.
/// </summary>
public sealed class SecureTransport : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly PeerIdentity _localIdentity;

    private AesGcmCipher? _outboundCipher;
    private AesGcmCipher? _inboundCipher;
    private bool _isHandshakeComplete;
    private bool _disposed;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);

    public RemotePeerIdentity? RemotePeer { get; private set; }
    public bool IsConnected => _client.Connected && _isHandshakeComplete;

    public SecureTransport(TcpClient client, PeerIdentity localIdentity)
    {
        _client = client;
        _stream = client.GetStream();
        _localIdentity = localIdentity;
    }

    /// <summary>
    /// Perform handshake as initiator.
    /// </summary>
    public async Task<bool> HandshakeAsInitiatorAsync(CancellationToken ct = default)
    {
        using var handshake = new NoiseXXHandshake(_localIdentity);

        // Step 1: Send initiator hello
        var hello = handshake.CreateInitiatorHello();
        await SendRawFrameAsync(MessageType.HandshakeInit, hello, ct);

        // Step 2: Receive responder message
        var (type, responderMsg) = await ReceiveRawFrameAsync(ct);
        if (type != MessageType.HandshakeResponse)
            throw new InvalidOperationException($"Expected HandshakeResponse, got {type}");

        // Step 3: Process and send final message
        var finalMsg = handshake.ProcessResponderAndFinalize(responderMsg);
        await SendRawFrameAsync(MessageType.HandshakeFinal, finalMsg, ct);

        // Step 4: Receive handshake complete
        (type, _) = await ReceiveRawFrameAsync(ct);
        if (type != MessageType.HandshakeComplete)
            throw new InvalidOperationException($"Expected HandshakeComplete, got {type}");

        // Split to get transport ciphers
        (_outboundCipher, _inboundCipher) = handshake.Split(true);
        RemotePeer = handshake.RemotePeer;
        _isHandshakeComplete = true;

        return true;
    }

    /// <summary>
    /// Perform handshake as responder.
    /// </summary>
    public async Task<bool> HandshakeAsResponderAsync(CancellationToken ct = default)
    {
        using var handshake = new NoiseXXHandshake(_localIdentity);

        // Step 1: Receive initiator hello
        var (type, initiatorHello) = await ReceiveRawFrameAsync(ct);
        if (type != MessageType.HandshakeInit)
            throw new InvalidOperationException($"Expected HandshakeInit, got {type}");

        // Step 2: Process and send response
        var responseMsg = handshake.ProcessInitiatorHelloAndRespond(initiatorHello);
        await SendRawFrameAsync(MessageType.HandshakeResponse, responseMsg, ct);

        // Step 3: Receive final message
        (type, var finalMsg) = await ReceiveRawFrameAsync(ct);
        if (type != MessageType.HandshakeFinal)
            throw new InvalidOperationException($"Expected HandshakeFinal, got {type}");

        handshake.ProcessInitiatorFinal(finalMsg);

        // Send handshake complete
        await SendRawFrameAsync(MessageType.HandshakeComplete, [], ct);

        // Split to get transport ciphers
        (_outboundCipher, _inboundCipher) = handshake.Split(false);
        RemotePeer = handshake.RemotePeer;
        _isHandshakeComplete = true;

        return true;
    }

    /// <summary>
    /// Send an encrypted message.
    /// </summary>
    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!_isHandshakeComplete)
            throw new InvalidOperationException("Handshake not complete");

        await _sendLock.WaitAsync(ct);
        try
        {
            var plainFrame = ProtocolFraming.CreateFrame(type, payload.Span);
            var encrypted = _outboundCipher!.Encrypt(plainFrame);

            // Send length + encrypted data
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, encrypted.Length);

            await _stream.WriteAsync(lengthBytes, ct);
            await _stream.WriteAsync(encrypted, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Receive an encrypted message.
    /// </summary>
    public async Task<(MessageType type, byte[] payload)> ReceiveAsync(CancellationToken ct = default)
    {
        if (!_isHandshakeComplete)
            throw new InvalidOperationException("Handshake not complete");

        await _receiveLock.WaitAsync(ct);
        try
        {
            // Read length
            var lengthBytes = new byte[4];
            await ReadExactlyAsync(_stream, lengthBytes, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

            if (length <= 0 || length > ProtocolFraming.MaxPayloadSize + 32)
                throw new InvalidOperationException($"Invalid message length: {length}");

            // Read encrypted data
            var encrypted = new byte[length];
            await ReadExactlyAsync(_stream, encrypted, ct);

            // Decrypt
            var plainFrame = _inboundCipher!.Decrypt(encrypted);
            return ProtocolFraming.ParseFrame(plainFrame);
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    private async Task SendRawFrameAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        var frame = ProtocolFraming.CreateFrame(type, payload);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, frame.Length);

        await _stream.WriteAsync(lengthBytes, ct);
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task<(MessageType type, byte[] payload)> ReceiveRawFrameAsync(CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(_stream, lengthBytes, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        if (length <= 0 || length > ProtocolFraming.MaxPayloadSize + ProtocolFraming.HeaderSize)
            throw new InvalidOperationException($"Invalid frame length: {length}");

        var frame = new byte[length];
        await ReadExactlyAsync(_stream, frame, ct);

        return ProtocolFraming.ParseFrame(frame);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection closed");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _outboundCipher?.Dispose();
            _inboundCipher?.Dispose();
            _stream.Dispose();
            _client.Dispose();
            _sendLock.Dispose();
            _receiveLock.Dispose();
            _disposed = true;
        }
    }
}
