using System.Security.Cryptography;
using System.Text;
using SyncBeam.P2P.Core;

namespace SyncBeam.P2P.Handshake;

/// <summary>
/// Noise Protocol Framework state machine.
/// Implements the symmetric state for hash chaining and key derivation.
/// </summary>
public sealed class SymmetricState : IDisposable
{
    private const int HashLen = 32;
    private const int KeyLen = 32;

    private byte[] _chainingKey;
    private byte[] _handshakeHash;
    private AesGcmCipher? _cipher;
    private ulong _nonce;
    private bool _hasKey;
    private bool _disposed;

    public byte[] HandshakeHash => _handshakeHash;

    public SymmetricState(string protocolName)
    {
        var protocolBytes = Encoding.UTF8.GetBytes(protocolName);

        if (protocolBytes.Length <= HashLen)
        {
            _handshakeHash = new byte[HashLen];
            protocolBytes.CopyTo(_handshakeHash, 0);
        }
        else
        {
            _handshakeHash = SHA256.HashData(protocolBytes);
        }

        _chainingKey = new byte[HashLen];
        _handshakeHash.CopyTo(_chainingKey, 0);
        _hasKey = false;
        _nonce = 0;
    }

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, tempK) = HkdfExpand2(_chainingKey, inputKeyMaterial);
        _chainingKey = ck;
        InitializeKey(tempK);
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(_handshakeHash);
        sha256.AppendData(data);
        _handshakeHash = sha256.GetHashAndReset();
    }

    public void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, tempH, tempK) = HkdfExpand3(_chainingKey, inputKeyMaterial);
        _chainingKey = ck;
        MixHash(tempH);
        InitializeKey(tempK);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        byte[] ciphertext;

        if (_hasKey)
        {
            ciphertext = _cipher!.EncryptWithNonce(_nonce++, plaintext, _handshakeHash);
        }
        else
        {
            ciphertext = plaintext.ToArray();
        }

        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        byte[] plaintext;

        if (_hasKey)
        {
            plaintext = _cipher!.DecryptWithNonce(_nonce++, ciphertext, _handshakeHash);
        }
        else
        {
            plaintext = ciphertext.ToArray();
        }

        MixHash(ciphertext);
        return plaintext;
    }

    public (AesGcmCipher outbound, AesGcmCipher inbound) Split()
    {
        var (tempK1, tempK2) = HkdfExpand2(_chainingKey, ReadOnlySpan<byte>.Empty);
        return (new AesGcmCipher(tempK1), new AesGcmCipher(tempK2));
    }

    private void InitializeKey(byte[] key)
    {
        _cipher?.Dispose();
        _cipher = new AesGcmCipher(key);
        _nonce = 0;
        _hasKey = true;
    }

    private static (byte[] k1, byte[] k2) HkdfExpand2(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var ikm = inputKeyMaterial.ToArray();
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, chainingKey);
        var output = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeyLen * 2, []);

        var k1 = output[..KeyLen];
        var k2 = output[KeyLen..];
        return (k1, k2);
    }

    private static (byte[] k1, byte[] k2, byte[] k3) HkdfExpand3(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var ikm = inputKeyMaterial.ToArray();
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, chainingKey);
        var output = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeyLen * 3, []);

        var k1 = output[..KeyLen];
        var k2 = output[KeyLen..(KeyLen * 2)];
        var k3 = output[(KeyLen * 2)..];
        return (k1, k2, k3);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cipher?.Dispose();
            CryptographicOperations.ZeroMemory(_chainingKey);
            _disposed = true;
        }
    }
}
