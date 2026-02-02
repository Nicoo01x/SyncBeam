using System.Buffers.Binary;
using MessagePack;

namespace SyncBeam.P2P.Transport;

/// <summary>
/// Protocol message types for SyncBeam P2P communication.
/// </summary>
public enum MessageType : byte
{
    // Handshake
    HandshakeInit = 0x01,
    HandshakeResponse = 0x02,
    HandshakeFinal = 0x03,
    HandshakeComplete = 0x04,

    // Control
    Ping = 0x10,
    Pong = 0x11,
    Disconnect = 0x12,

    // File Transfer
    FileAnnounce = 0x20,
    FileRequest = 0x21,
    FileChunk = 0x22,
    FileChunkAck = 0x23,
    FileComplete = 0x24,
    FileCancel = 0x25,
    FileResume = 0x26,

    // Clipboard
    ClipboardData = 0x30,
    ClipboardAck = 0x31
}

/// <summary>
/// Wire protocol for framed messages.
/// Format: [Length:4][Type:1][Payload:N]
/// </summary>
public static class ProtocolFraming
{
    public const int HeaderSize = 5;
    public const int MaxPayloadSize = 16 * 1024 * 1024; // 16 MB max

    public static byte[] CreateFrame(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentException($"Payload too large: {payload.Length} > {MaxPayloadSize}");

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        frame[4] = (byte)type;
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static (MessageType type, byte[] payload) ParseFrame(byte[] frame)
    {
        if (frame.Length < HeaderSize)
            throw new ArgumentException("Frame too short");

        var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, 4));
        var type = (MessageType)frame[4];
        var payload = frame.AsSpan(5, length).ToArray();

        return (type, payload);
    }
}

[MessagePackObject]
public class FileAnnounceMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required string FileName { get; set; }

    [Key(2)]
    public required long FileSize { get; set; }

    [Key(3)]
    public required byte[] FileHash { get; set; }

    [Key(4)]
    public required int ChunkSize { get; set; }

    [Key(5)]
    public required long TotalChunks { get; set; }

    [Key(6)]
    public string? MimeType { get; set; }
}

[MessagePackObject]
public class FileRequestMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required long ChunkIndex { get; set; }

    [Key(2)]
    public int ChunkCount { get; set; } = 1;
}

[MessagePackObject]
public class FileChunkMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required long ChunkIndex { get; set; }

    [Key(2)]
    public required byte[] Data { get; set; }

    [Key(3)]
    public required byte[] ChunkHash { get; set; }
}

[MessagePackObject]
public class FileChunkAckMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required long ChunkIndex { get; set; }

    [Key(2)]
    public required bool Success { get; set; }
}

[MessagePackObject]
public class FileCompleteMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required bool Success { get; set; }

    [Key(2)]
    public string? ErrorMessage { get; set; }
}

[MessagePackObject]
public class FileResumeMessage
{
    [Key(0)]
    public required string TransferId { get; set; }

    [Key(1)]
    public required long LastReceivedChunk { get; set; }
}

[MessagePackObject]
public class ClipboardDataMessage
{
    [Key(0)]
    public required string ClipboardId { get; set; }

    [Key(1)]
    public required ClipboardContentType ContentType { get; set; }

    [Key(2)]
    public required byte[] Data { get; set; }

    [Key(3)]
    public long Timestamp { get; set; }
}

public enum ClipboardContentType : byte
{
    Text = 0,
    Image = 1,
    Rtf = 2,
    Html = 3,
    Files = 4
}

[MessagePackObject]
public class PingMessage
{
    [Key(0)]
    public required long Timestamp { get; set; }

    [Key(1)]
    public required long SequenceNumber { get; set; }
}

[MessagePackObject]
public class PongMessage
{
    [Key(0)]
    public required long PingTimestamp { get; set; }

    [Key(1)]
    public required long SequenceNumber { get; set; }
}
