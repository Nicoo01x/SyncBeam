using System.Security.Cryptography;
using NSec.Cryptography;

namespace SyncBeam.P2P.Core;

/// <summary>
/// Represents a peer's cryptographic identity using Ed25519.
/// </summary>
public sealed class PeerIdentity : IDisposable
{
    private readonly Key _privateKey;
    private bool _disposed;

    public PublicKey PublicKey { get; }
    public byte[] PublicKeyBytes => PublicKey.Export(KeyBlobFormat.RawPublicKey);
    public string PeerId => Convert.ToHexString(SHA256.HashData(PublicKeyBytes)[..16]).ToLowerInvariant();

    private PeerIdentity(Key privateKey)
    {
        _privateKey = privateKey;
        PublicKey = privateKey.PublicKey;
    }

    /// <summary>
    /// Generates a new random peer identity.
    /// </summary>
    public static PeerIdentity Generate()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        return new PeerIdentity(key);
    }

    /// <summary>
    /// Loads a peer identity from exported key bytes.
    /// </summary>
    public static PeerIdentity FromPrivateKey(byte[] privateKeyBytes)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = Key.Import(algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new PeerIdentity(key);
    }

    /// <summary>
    /// Exports the private key for storage.
    /// </summary>
    public byte[] ExportPrivateKey()
    {
        return _privateKey.Export(KeyBlobFormat.RawPrivateKey);
    }

    /// <summary>
    /// Signs data with this identity's private key.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        return algorithm.Sign(_privateKey, data);
    }

    /// <summary>
    /// Verifies a signature against a public key.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var pubKey = PublicKey.Import(algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        return algorithm.Verify(pubKey, data, signature);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _privateKey.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents a remote peer's public identity (no private key).
/// </summary>
public sealed class RemotePeerIdentity
{
    public byte[] PublicKeyBytes { get; }
    public string PeerId { get; }

    public RemotePeerIdentity(byte[] publicKeyBytes)
    {
        PublicKeyBytes = publicKeyBytes;
        PeerId = Convert.ToHexString(SHA256.HashData(publicKeyBytes)[..16]).ToLowerInvariant();
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        return PeerIdentity.Verify(PublicKeyBytes, data, signature);
    }
}
