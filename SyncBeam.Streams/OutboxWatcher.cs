namespace SyncBeam.Streams;

/// <summary>
/// Monitors the outbox directory and automatically beams files to all connected peers.
/// </summary>
public sealed class OutboxWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly FileTransferEngine _transferEngine;
    private readonly HashSet<string> _processingFiles = new();
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<FileDetectedEventArgs>? FileDetected;

    public OutboxWatcher(FileTransferEngine transferEngine, string outboxPath)
    {
        _transferEngine = transferEngine;

        Directory.CreateDirectory(outboxPath);

        _watcher = new FileSystemWatcher(outboxPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };

        _watcher.Created += OnFileCreated;
        _watcher.Renamed += OnFileRenamed;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;

        // Process any existing files in the outbox
        foreach (var file in Directory.GetFiles(_watcher.Path))
        {
            _ = ProcessFileAsync(file);
        }
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Created)
        {
            _ = ProcessFileAsync(e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Handle files that are renamed into the outbox (common with drag-drop)
        _ = ProcessFileAsync(e.FullPath);
    }

    private async Task ProcessFileAsync(string filePath)
    {
        // Skip temp files and hidden files
        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith('.') || fileName.StartsWith("~"))
            return;

        // Ensure we don't process the same file twice
        lock (_lock)
        {
            if (_processingFiles.Contains(filePath))
                return;
            _processingFiles.Add(filePath);
        }

        try
        {
            // Wait for file to be fully written (up to 30 seconds)
            if (!await WaitForFileReadyAsync(filePath, TimeSpan.FromSeconds(30)))
            {
                return;
            }

            FileDetected?.Invoke(this, new FileDetectedEventArgs { FilePath = filePath });

            // Announce the file
            var transferId = await _transferEngine.AnnounceFileAsync(filePath);

            // Optionally move to a "sent" folder or delete after transfer
            // For now, we leave the file in place
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing outbox file {filePath}: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _processingFiles.Remove(filePath);
            }
        }
    }

    private static async Task<bool> WaitForFileReadyAsync(string filePath, TimeSpan timeout)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                // Try to open the file exclusively
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (FileNotFoundException)
            {
                // File was deleted
                return false;
            }
            catch (IOException)
            {
                // File is still being written
                await Task.Delay(500);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _watcher.Dispose();
            _disposed = true;
        }
    }
}

public class FileDetectedEventArgs : EventArgs
{
    public required string FilePath { get; init; }
}
