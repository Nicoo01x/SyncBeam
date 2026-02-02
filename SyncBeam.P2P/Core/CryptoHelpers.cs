using System.Security.Cryptography;
using NSec.Cryptography;

namespace SyncBeam.P2P.Core;

/// <summary>
/// Cryptographic helper functions for SyncBeam.
/// </summary>
public static class CryptoHelpers
{
    /// <summary>
    /// Computes SHA256 hash of the project secret for peer identification.
    /// </summary>
    public static byte[] ComputeSecretHash(string projectSecret)
    {
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectSecret));
    }

    /// <summary>
    /// Generates cryptographically secure random bytes.
    /// </summary>
    public static byte[] GenerateRandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>
    /// Performs X25519 key exchange to derive shared secret.
    /// </summary>
    public static byte[] X25519KeyExchange(byte[] privateKey, byte[] remotePublicKey)
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var privKey = Key.Import(algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        var pubKey = PublicKey.Import(algorithm, remotePublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = algorithm.Agree(privKey, pubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        return sharedSecret.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    /// <summary>
    /// Generates an X25519 keypair for key exchange.
    /// </summary>
    public static (byte[] privateKey, byte[] publicKey) GenerateX25519KeyPair()
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        return (
            key.Export(KeyBlobFormat.RawPrivateKey),
            key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
        );
    }

    /// <summary>
    /// HKDF key derivation.
    /// </summary>
    public static byte[] HkdfDerive(byte[] inputKeyMaterial, byte[] salt, byte[] info, int outputLength)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, outputLength, salt, info);
    }

    /// <summary>
    /// Constant-time comparison of two byte arrays.
    /// </summary>
    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
