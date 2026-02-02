using System.Collections.Concurrent;
using System.Security.Cryptography;
using MessagePack;
using SyncBeam.P2P;
using SyncBeam.P2P.Transport;

namespace SyncBeam.Streams;

/// <summary>
/// File transfer engine supporting chunked streaming, resume, and O(1) memory usage.
/// Implements a pull-based model where the receiver requests chunks from the sender.
/// </summary>
public sealed class FileTransferEngine : IDisposable
{
    private const int DefaultChunkSize = 256 * 1024; // 256 KB
    private const int MinChunkSize = 64 * 1024; // 64 KB
    private const int MaxChunkSize = 1024 * 1024; // 1 MB
    private const int MaxConcurrentChunks = 8;

    private readonly PeerManager _peerManager;
    private readonly string _inboxPath;
    private readonly string _outboxPath;
    private readonly ConcurrentDictionary<string, OutgoingTransfer> _outgoingTransfers = new();
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incomingTransfers = new();
    private readonly ConcurrentDictionary<string, TransferCheckpoint> _checkpoints = new();
    private bool _disposed;

    public event EventHandler<TransferProgressEventArgs>? TransferProgress;
    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;
    public event EventHandler<FileAnnouncedEventArgs>? FileAnnounced;

    public FileTransferEngine(PeerManager peerManager, string basePath)
    {
        _peerManager = peerManager;
        _inboxPath = Path.Combine(basePath, "inbox");
        _outboxPath = Path.Combine(basePath, "outbox");

        Directory.CreateDirectory(_inboxPath);
        Directory.CreateDirectory(_outboxPath);

        _peerManager.MessageReceived += OnMessageReceived;

        LoadCheckpoints();
    }

    /// <summary>
    /// Announce a file to all connected peers.
    /// </summary>
    public async Task<string> AnnounceFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        var fileInfo = new FileInfo(filePath);
        var transferId = Guid.NewGuid().ToString("N");
        var fileHash = await ComputeFileHashAsync(filePath, ct);
        var chunkSize = CalculateOptimalChunkSize(fileInfo.Length);
        var totalChunks = (long)Math.Ceiling((double)fileInfo.Length / chunkSize);

        var transfer = new OutgoingTransfer
        {
            TransferId = transferId,
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileHash = fileHash,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks
        };

        _outgoingTransfers.TryAdd(transferId, transfer);

        var announcement = new FileAnnounceMessage
        {
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileHash = fileHash,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            MimeType = GetMimeType(fileInfo.Extension)
        };

        await _peerManager.BroadcastAsync(MessageType.FileAnnounce, announcement);

        return transferId;
    }

    /// <summary>
    /// Request to receive an announced file from a peer.
    /// </summary>
    public async Task AcceptFileAsync(string peerId, string transferId, CancellationToken ct = default)
    {
        // Check if we have a checkpoint for this transfer
        var startChunk = 0L;
        if (_checkpoints.TryGetValue(transferId, out var checkpoint))
        {
            startChunk = checkpoint.LastCompletedChunk + 1;
        }

        var request = new FileRequestMessage
        {
            TransferId = transferId,
            ChunkIndex = startChunk,
            ChunkCount = MaxConcurrentChunks
        };

        await _peerManager.SendAsync(peerId, MessageType.FileRequest, request);
    }

    /// <summary>
    /// Cancel an ongoing transfer.
    /// </summary>
    public async Task CancelTransferAsync(string peerId, string transferId)
    {
        _outgoingTransfers.TryRemove(transferId, out _);

        if (_incomingTransfers.TryRemove(transferId, out var incoming))
        {
            incoming.Dispose();
        }

        var cancelMsg = new FileCompleteMessage
        {
            TransferId = transferId,
            Success = false,
            ErrorMessage = "Transfer cancelled"
        };

        await _peerManager.SendAsync(peerId, MessageType.FileCancel, cancelMsg);
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        try
        {
            switch (e.Type)
            {
                case MessageType.FileAnnounce:
                    HandleFileAnnounce(e.PeerId!, e.Payload);
                    break;

                case MessageType.FileRequest:
                    _ = HandleFileRequestAsync(e.PeerId!, e.Payload);
                    break;

                case MessageType.FileChunk:
                    _ = HandleFileChunkAsync(e.PeerId!, e.Payload);
                    break;

                case MessageType.FileChunkAck:
                    HandleFileChunkAck(e.PeerId!, e.Payload);
                    break;

                case MessageType.FileComplete:
                    HandleFileComplete(e.PeerId!, e.Payload);
                    break;

                case MessageType.FileResume:
                    _ = HandleFileResumeAsync(e.PeerId!, e.Payload);
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void HandleFileAnnounce(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileAnnounceMessage>(payload);

        FileAnnounced?.Invoke(this, new FileAnnouncedEventArgs
        {
            PeerId = peerId,
            TransferId = msg.TransferId,
            FileName = msg.FileName,
            FileSize = msg.FileSize,
            MimeType = msg.MimeType
        });
    }

    private async Task HandleFileRequestAsync(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileRequestMessage>(payload);

        if (!_outgoingTransfers.TryGetValue(msg.TransferId, out var transfer))
            return;

        // Send requested chunks
        for (int i = 0; i < msg.ChunkCount && msg.ChunkIndex + i < transfer.TotalChunks; i++)
        {
            var chunkIndex = msg.ChunkIndex + i;
            await SendChunkAsync(peerId, transfer, chunkIndex);
        }
    }

    private async Task SendChunkAsync(string peerId, OutgoingTransfer transfer, long chunkIndex)
    {
        var offset = chunkIndex * transfer.ChunkSize;
        var length = (int)Math.Min(transfer.ChunkSize, transfer.FileSize - offset);

        var buffer = new byte[length];
        using (var fs = new FileStream(transfer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            await fs.ReadExactlyAsync(buffer.AsMemory(0, length));
        }

        var chunkHash = SHA256.HashData(buffer);

        var chunk = new FileChunkMessage
        {
            TransferId = transfer.TransferId,
            ChunkIndex = chunkIndex,
            Data = buffer,
            ChunkHash = chunkHash
        };

        await _peerManager.SendAsync(peerId, MessageType.FileChunk, chunk);
    }

    private async Task HandleFileChunkAsync(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileChunkMessage>(payload);

        if (!_incomingTransfers.TryGetValue(msg.TransferId, out var transfer))
        {
            // Start new incoming transfer if we have metadata from announce
            // For now, we'll need the announce to set up the transfer first
            return;
        }

        // Verify chunk hash
        var computedHash = SHA256.HashData(msg.Data);
        if (!CryptographicOperations.FixedTimeEquals(computedHash, msg.ChunkHash))
        {
            // Request retry
            var ack = new FileChunkAckMessage
            {
                TransferId = msg.TransferId,
                ChunkIndex = msg.ChunkIndex,
                Success = false
            };
            await _peerManager.SendAsync(peerId, MessageType.FileChunkAck, ack);
            return;
        }

        // Write chunk to file
        await transfer.WriteChunkAsync(msg.ChunkIndex, msg.Data);

        // Send acknowledgment
        var successAck = new FileChunkAckMessage
        {
            TransferId = msg.TransferId,
            ChunkIndex = msg.ChunkIndex,
            Success = true
        };
        await _peerManager.SendAsync(peerId, MessageType.FileChunkAck, successAck);

        // Update progress
        transfer.ReceivedChunks++;
        var progress = (double)transfer.ReceivedChunks / transfer.TotalChunks * 100;

        TransferProgress?.Invoke(this, new TransferProgressEventArgs
        {
            TransferId = msg.TransferId,
            FileName = transfer.FileName,
            Progress = progress,
            BytesTransferred = transfer.ReceivedChunks * transfer.ChunkSize,
            TotalBytes = transfer.FileSize
        });

        // Save checkpoint
        SaveCheckpoint(msg.TransferId, msg.ChunkIndex);

        // Request more chunks if needed
        if (transfer.ReceivedChunks < transfer.TotalChunks)
        {
            var nextChunk = msg.ChunkIndex + MaxConcurrentChunks;
            if (nextChunk < transfer.TotalChunks)
            {
                var request = new FileRequestMessage
                {
                    TransferId = msg.TransferId,
                    ChunkIndex = nextChunk,
                    ChunkCount = MaxConcurrentChunks
                };
                await _peerManager.SendAsync(peerId, MessageType.FileRequest, request);
            }
        }

        // Check if transfer is complete
        if (transfer.ReceivedChunks >= transfer.TotalChunks)
        {
            await CompleteIncomingTransferAsync(peerId, transfer);
        }
    }

    private void HandleFileChunkAck(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileChunkAckMessage>(payload);

        if (!_outgoingTransfers.TryGetValue(msg.TransferId, out var transfer))
            return;

        if (msg.Success)
        {
            transfer.AcknowledgedChunks++;

            var progress = (double)transfer.AcknowledgedChunks / transfer.TotalChunks * 100;

            TransferProgress?.Invoke(this, new TransferProgressEventArgs
            {
                TransferId = msg.TransferId,
                FileName = transfer.FileName,
                Progress = progress,
                BytesTransferred = transfer.AcknowledgedChunks * transfer.ChunkSize,
                TotalBytes = transfer.FileSize
            });

            if (transfer.AcknowledgedChunks >= transfer.TotalChunks)
            {
                TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
                {
                    TransferId = msg.TransferId,
                    FileName = transfer.FileName,
                    Success = true,
                    FilePath = transfer.FilePath
                });

                _outgoingTransfers.TryRemove(msg.TransferId, out _);
            }
        }
        else
        {
            // Retry the chunk
            _ = SendChunkAsync(peerId, transfer, msg.ChunkIndex);
        }
    }

    private void HandleFileComplete(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileCompleteMessage>(payload);

        if (_incomingTransfers.TryRemove(msg.TransferId, out var incoming))
        {
            incoming.Dispose();
        }

        _outgoingTransfers.TryRemove(msg.TransferId, out _);
        _checkpoints.TryRemove(msg.TransferId, out _);

        TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
        {
            TransferId = msg.TransferId,
            Success = msg.Success,
            ErrorMessage = msg.ErrorMessage
        });
    }

    private async Task HandleFileResumeAsync(string peerId, byte[] payload)
    {
        var msg = MessagePackSerializer.Deserialize<FileResumeMessage>(payload);

        if (!_outgoingTransfers.TryGetValue(msg.TransferId, out var transfer))
            return;

        // Send chunks starting from the resume point
        for (int i = 0; i < MaxConcurrentChunks && msg.LastReceivedChunk + 1 + i < transfer.TotalChunks; i++)
        {
            await SendChunkAsync(peerId, transfer, msg.LastReceivedChunk + 1 + i);
        }
    }

    private async Task CompleteIncomingTransferAsync(string peerId, IncomingTransfer transfer)
    {
        transfer.FileStream.Close();

        // Verify final file hash
        var finalHash = await ComputeFileHashAsync(transfer.TempFilePath, CancellationToken.None);

        if (!CryptographicOperations.FixedTimeEquals(finalHash, transfer.FileHash))
        {
            // Hash mismatch - delete file and report error
            File.Delete(transfer.TempFilePath);

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                TransferId = transfer.TransferId,
                FileName = transfer.FileName,
                Success = false,
                ErrorMessage = "File hash verification failed"
            });

            return;
        }

        // Move to inbox with unique name
        var finalPath = GetUniqueFilePath(Path.Combine(_inboxPath, transfer.FileName));
        File.Move(transfer.TempFilePath, finalPath);

        // Clean up
        _incomingTransfers.TryRemove(transfer.TransferId, out _);
        _checkpoints.TryRemove(transfer.TransferId, out _);

        // Notify sender of completion
        var complete = new FileCompleteMessage
        {
            TransferId = transfer.TransferId,
            Success = true
        };
        await _peerManager.SendAsync(peerId, MessageType.FileComplete, complete);

        TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
        {
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Success = true,
            FilePath = finalPath
        });
    }

    /// <summary>
    /// Initialize an incoming transfer from a file announcement.
    /// </summary>
    public void PrepareIncomingTransfer(string peerId, FileAnnounceMessage announcement)
    {
        var tempPath = Path.Combine(_inboxPath, $".{announcement.TransferId}.tmp");

        var transfer = new IncomingTransfer
        {
            TransferId = announcement.TransferId,
            PeerId = peerId,
            FileName = announcement.FileName,
            FileSize = announcement.FileSize,
            FileHash = announcement.FileHash,
            ChunkSize = announcement.ChunkSize,
            TotalChunks = announcement.TotalChunks,
            TempFilePath = tempPath,
            FileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
        };

        // Pre-allocate file
        transfer.FileStream.SetLength(announcement.FileSize);

        _incomingTransfers.TryAdd(announcement.TransferId, transfer);
    }

    private static async Task<byte[]> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return await sha256.ComputeHashAsync(fs, ct);
    }

    private static int CalculateOptimalChunkSize(long fileSize)
    {
        // Adaptive chunk size based on file size
        if (fileSize < 1024 * 1024) // < 1 MB
            return MinChunkSize;
        if (fileSize < 100 * 1024 * 1024) // < 100 MB
            return DefaultChunkSize;
        return MaxChunkSize;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        int counter = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".zip" => "application/zip",
            ".doc" or ".docx" => "application/msword",
            _ => "application/octet-stream"
        };
    }

    private void SaveCheckpoint(string transferId, long lastChunk)
    {
        var checkpoint = new TransferCheckpoint
        {
            TransferId = transferId,
            LastCompletedChunk = lastChunk,
            Timestamp = DateTimeOffset.UtcNow
        };

        _checkpoints.AddOrUpdate(transferId, checkpoint, (_, _) => checkpoint);

        // Persist to disk
        var checkpointPath = Path.Combine(_inboxPath, $".{transferId}.checkpoint");
        var json = System.Text.Json.JsonSerializer.Serialize(checkpoint);
        File.WriteAllText(checkpointPath, json);
    }

    private void LoadCheckpoints()
    {
        var checkpointFiles = Directory.GetFiles(_inboxPath, "*.checkpoint");
        foreach (var file in checkpointFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var checkpoint = System.Text.Json.JsonSerializer.Deserialize<TransferCheckpoint>(json);
                if (checkpoint != null)
                {
                    _checkpoints.TryAdd(checkpoint.TransferId, checkpoint);
                }
            }
            catch
            {
                // Ignore corrupt checkpoint files
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var transfer in _incomingTransfers.Values)
            {
                transfer.Dispose();
            }
            _incomingTransfers.Clear();
            _outgoingTransfers.Clear();
            _disposed = true;
        }
    }
}

internal class OutgoingTransfer
{
    public required string TransferId { get; set; }
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public required byte[] FileHash { get; set; }
    public required int ChunkSize { get; set; }
    public required long TotalChunks { get; set; }
    public long AcknowledgedChunks { get; set; }
}

internal class IncomingTransfer : IDisposable
{
    public required string TransferId { get; set; }
    public required string PeerId { get; set; }
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public required byte[] FileHash { get; set; }
    public required int ChunkSize { get; set; }
    public required long TotalChunks { get; set; }
    public required string TempFilePath { get; set; }
    public required FileStream FileStream { get; set; }
    public long ReceivedChunks { get; set; }

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteChunkAsync(long chunkIndex, byte[] data)
    {
        await _writeLock.WaitAsync();
        try
        {
            var offset = chunkIndex * ChunkSize;
            FileStream.Seek(offset, SeekOrigin.Begin);
            await FileStream.WriteAsync(data);
            await FileStream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        FileStream.Dispose();
        _writeLock.Dispose();
    }
}

internal class TransferCheckpoint
{
    public required string TransferId { get; set; }
    public required long LastCompletedChunk { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
}

public class TransferProgressEventArgs : EventArgs
{
    public required string TransferId { get; init; }
    public string? FileName { get; init; }
    public required double Progress { get; init; }
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
}

public class TransferCompletedEventArgs : EventArgs
{
    public required string TransferId { get; init; }
    public string? FileName { get; init; }
    public required bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? ErrorMessage { get; init; }
}

public class FileAnnouncedEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public string? MimeType { get; init; }
}
