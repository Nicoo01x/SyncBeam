using System.Buffers.Binary;
using SyncBeam.P2P.Core;

namespace SyncBeam.P2P.Handshake;

/// <summary>
/// Noise XX handshake implementation.
/// Pattern: XX = -> e; <- e, ee, s, es; -> s, se
/// Provides mutual authentication with identity hiding.
/// </summary>
public sealed class NoiseXXHandshake : IDisposable
{
    private const string ProtocolName = "Noise_XX_25519_AESGCM_SHA256";
    private const int DhLen = 32;

    private readonly SymmetricState _symmetricState;
    private readonly PeerIdentity _localIdentity;
    private readonly byte[] _localSecretHash;

    private byte[]? _localEphemeralPrivate;
    private byte[]? _localEphemeralPublic;
    private byte[]? _remoteEphemeralPublic;
    private byte[]? _remoteStaticPublic;

    private bool _disposed;

    public RemotePeerIdentity? RemotePeer { get; private set; }
    public byte[]? RemoteSecretHash { get; private set; }

    public NoiseXXHandshake(PeerIdentity localIdentity, byte[] localSecretHash)
    {
        _localIdentity = localIdentity;
        _localSecretHash = localSecretHash;
        _symmetricState = new SymmetricState(ProtocolName);
    }

    /// <summary>
    /// Initiator: Create first message (-> e)
    /// </summary>
    public byte[] CreateInitiatorHello()
    {
        // Generate ephemeral keypair
        (_localEphemeralPrivate, _localEphemeralPublic) = CryptoHelpers.GenerateX25519KeyPair();

        // -> e
        _symmetricState.MixHash(_localEphemeralPublic);

        // Message: e (32 bytes)
        return _localEphemeralPublic;
    }

    /// <summary>
    /// Responder: Process first message, create response (<- e, ee, s, es)
    /// </summary>
    public byte[] ProcessInitiatorHelloAndRespond(ReadOnlySpan<byte> message)
    {
        if (message.Length != DhLen)
            throw new InvalidOperationException("Invalid initiator hello length");

        // <- e (receive)
        _remoteEphemeralPublic = message.ToArray();
        _symmetricState.MixHash(_remoteEphemeralPublic);

        // Generate our ephemeral
        (_localEphemeralPrivate, _localEphemeralPublic) = CryptoHelpers.GenerateX25519KeyPair();

        using var ms = new MemoryStream();

        // -> e
        _symmetricState.MixHash(_localEphemeralPublic);
        ms.Write(_localEphemeralPublic);

        // -> ee
        var ee = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate, _remoteEphemeralPublic);
        _symmetricState.MixKey(ee);

        // -> s (encrypted static public key)
        var encryptedS = _symmetricState.EncryptAndHash(_localIdentity.PublicKeyBytes);
        WriteWithLength(ms, encryptedS);

        // -> es
        var es = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate, _remoteEphemeralPublic);
        _symmetricState.MixKey(es);

        // Include secret hash in payload (encrypted)
        var payload = CreatePayload();
        var encryptedPayload = _symmetricState.EncryptAndHash(payload);
        WriteWithLength(ms, encryptedPayload);

        return ms.ToArray();
    }

    /// <summary>
    /// Initiator: Process response, create final message (-> s, se)
    /// </summary>
    public byte[] ProcessResponderAndFinalize(ReadOnlySpan<byte> message)
    {
        using var ms = new MemoryStream(message.ToArray());
        using var reader = new BinaryReader(ms);

        // <- e
        _remoteEphemeralPublic = reader.ReadBytes(DhLen);
        _symmetricState.MixHash(_remoteEphemeralPublic);

        // <- ee
        var ee = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate!, _remoteEphemeralPublic);
        _symmetricState.MixKey(ee);

        // <- s (decrypt remote static)
        var encryptedS = ReadWithLength(reader);
        _remoteStaticPublic = _symmetricState.DecryptAndHash(encryptedS);
        RemotePeer = new RemotePeerIdentity(_remoteStaticPublic);

        // <- es (using remote static)
        var es = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate!, _remoteStaticPublic);
        _symmetricState.MixKey(es);

        // Decrypt and verify payload
        var encryptedPayload = ReadWithLength(reader);
        var payload = _symmetricState.DecryptAndHash(encryptedPayload);
        ProcessPayload(payload);

        // Now create final message
        using var outMs = new MemoryStream();

        // -> s (our encrypted static)
        var ourEncryptedS = _symmetricState.EncryptAndHash(_localIdentity.PublicKeyBytes);
        WriteWithLength(outMs, ourEncryptedS);

        // -> se
        var se = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate!, _remoteStaticPublic);
        _symmetricState.MixKey(se);

        // Include our payload
        var ourPayload = CreatePayload();
        var ourEncryptedPayload = _symmetricState.EncryptAndHash(ourPayload);
        WriteWithLength(outMs, ourEncryptedPayload);

        return outMs.ToArray();
    }

    /// <summary>
    /// Responder: Process final message
    /// </summary>
    public void ProcessInitiatorFinal(ReadOnlySpan<byte> message)
    {
        using var ms = new MemoryStream(message.ToArray());
        using var reader = new BinaryReader(ms);

        // <- s
        var encryptedS = ReadWithLength(reader);
        _remoteStaticPublic = _symmetricState.DecryptAndHash(encryptedS);
        RemotePeer = new RemotePeerIdentity(_remoteStaticPublic);

        // <- se
        var se = CryptoHelpers.X25519KeyExchange(_localEphemeralPrivate!, _remoteStaticPublic);
        _symmetricState.MixKey(se);

        // Decrypt payload
        var encryptedPayload = ReadWithLength(reader);
        var payload = _symmetricState.DecryptAndHash(encryptedPayload);
        ProcessPayload(payload);
    }

    /// <summary>
    /// Split to get transport ciphers after handshake completes.
    /// </summary>
    public (AesGcmCipher outbound, AesGcmCipher inbound) Split(bool isInitiator)
    {
        var (c1, c2) = _symmetricState.Split();
        return isInitiator ? (c1, c2) : (c2, c1);
    }

    /// <summary>
    /// Verify that the remote peer has the same project secret.
    /// </summary>
    public bool VerifySecretHash()
    {
        if (RemoteSecretHash == null)
            return false;

        return CryptoHelpers.ConstantTimeEquals(_localSecretHash, RemoteSecretHash);
    }

    private byte[] CreatePayload()
    {
        // Payload: secret_hash (32) + timestamp (8) + signature of (handshake_hash + timestamp)
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(_localSecretHash);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        writer.Write(timestamp);

        // Sign handshake hash + timestamp for proof of identity
        var signData = new byte[_symmetricState.HandshakeHash.Length + 8];
        _symmetricState.HandshakeHash.CopyTo(signData, 0);
        BinaryPrimitives.WriteInt64BigEndian(signData.AsSpan(_symmetricState.HandshakeHash.Length), timestamp);

        var signature = _localIdentity.Sign(signData);
        writer.Write((ushort)signature.Length);
        writer.Write(signature);

        return ms.ToArray();
    }

    private void ProcessPayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);

        RemoteSecretHash = reader.ReadBytes(32);
        var timestamp = reader.ReadInt64();

        // Verify timestamp is within acceptable range (5 minutes)
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - timestamp) > 5 * 60 * 1000)
            throw new InvalidOperationException("Handshake timestamp out of range");

        var sigLen = reader.ReadUInt16();
        var signature = reader.ReadBytes(sigLen);

        // Verify signature
        var signData = new byte[_symmetricState.HandshakeHash.Length + 8];
        _symmetricState.HandshakeHash.CopyTo(signData, 0);
        BinaryPrimitives.WriteInt64BigEndian(signData.AsSpan(_symmetricState.HandshakeHash.Length), timestamp);

        if (!RemotePeer!.Verify(signData, signature))
            throw new InvalidOperationException("Invalid handshake signature");
    }

    private static void WriteWithLength(Stream stream, byte[] data)
    {
        Span<byte> lenBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes, (ushort)data.Length);
        stream.Write(lenBytes);
        stream.Write(data);
    }

    private static byte[] ReadWithLength(BinaryReader reader)
    {
        var len = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
        return reader.ReadBytes(len);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _symmetricState.Dispose();
            if (_localEphemeralPrivate != null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_localEphemeralPrivate);
            _disposed = true;
        }
    }
}
