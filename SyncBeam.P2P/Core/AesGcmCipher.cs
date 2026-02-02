using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SyncBeam.P2P.Core;

/// <summary>
/// AES-256-GCM cipher implementation for Noise Protocol and session encryption.
/// Thread-safe with nonce management.
/// </summary>
public sealed class AesGcmCipher : IDisposable
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits
    private const int TagSize = 16; // 128 bits

    private readonly AesGcm _aes;
    private readonly byte[] _key;
    private ulong _nonce;
    private readonly object _lock = new();
    private bool _disposed;

    public AesGcmCipher(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));

        _key = new byte[KeySize];
        key.CopyTo(_key, 0);
        _aes = new AesGcm(_key, TagSize);
        _nonce = 0;
    }

    /// <summary>
    /// Encrypts plaintext with optional associated data.
    /// Returns: nonce (12) + ciphertext + tag (16)
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var nonce = new byte[NonceSize];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _nonce++);

            var output = new byte[NonceSize + plaintext.Length + TagSize];
            nonce.CopyTo(output, 0);

            var ciphertext = output.AsSpan(NonceSize, plaintext.Length);
            var tag = output.AsSpan(NonceSize + plaintext.Length, TagSize);

            _aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            return output;
        }
    }

    /// <summary>
    /// Decrypts ciphertext with optional associated data.
    /// Input format: nonce (12) + ciphertext + tag (16)
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> encryptedData, ReadOnlySpan<byte> associatedData = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (encryptedData.Length < NonceSize + TagSize)
                throw new CryptographicException("Invalid encrypted data length");

            var nonce = encryptedData[..NonceSize];
            var ciphertext = encryptedData[NonceSize..^TagSize];
            var tag = encryptedData[^TagSize..];

            var plaintext = new byte[ciphertext.Length];
            _aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            return plaintext;
        }
    }

    /// <summary>
    /// Encrypts with an explicit nonce (for Noise protocol).
    /// </summary>
    public byte[] EncryptWithNonce(ulong nonceValue, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var nonce = new byte[NonceSize];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), nonceValue);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            _aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var output = new byte[ciphertext.Length + TagSize];
            ciphertext.CopyTo(output, 0);
            tag.CopyTo(output, ciphertext.Length);

            return output;
        }
    }

    /// <summary>
    /// Decrypts with an explicit nonce (for Noise protocol).
    /// Input format: ciphertext + tag (16)
    /// </summary>
    public byte[] DecryptWithNonce(ulong nonceValue, ReadOnlySpan<byte> encryptedData, ReadOnlySpan<byte> associatedData = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (encryptedData.Length < TagSize)
                throw new CryptographicException("Invalid encrypted data length");

            var nonce = new byte[NonceSize];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), nonceValue);

            var ciphertext = encryptedData[..^TagSize];
            var tag = encryptedData[^TagSize..];

            var plaintext = new byte[ciphertext.Length];
            _aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            return plaintext;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}
