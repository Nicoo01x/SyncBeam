using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MessagePack;
using SyncBeam.P2P;
using SyncBeam.P2P.Transport;

namespace SyncBeam.Clipboard;

/// <summary>
/// Background clipboard watcher that monitors and syncs clipboard content across peers.
/// Supports text, images, RTF, and HTML content.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly PeerManager _peerManager;
    private readonly Thread _watcherThread;
    private readonly CancellationTokenSource _cts;
    private HwndSource? _hwndSource;
    private IntPtr _nextClipboardViewer;
    private string? _lastClipboardHash;
    private bool _isEnabled = true;
    private bool _disposed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public event EventHandler<ClipboardReceivedEventArgs>? ClipboardReceived;

    public ClipboardWatcher(PeerManager peerManager)
    {
        _peerManager = peerManager;
        _cts = new CancellationTokenSource();

        _peerManager.MessageReceived += OnMessageReceived;

        _watcherThread = new Thread(WatcherThreadProc)
        {
            IsBackground = true,
            Name = "ClipboardWatcher"
        };
        _watcherThread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _watcherThread.Start();
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();

        // Shutdown the dispatcher on the watcher thread to exit the message loop
        _hwndSource?.Dispatcher.InvokeAsync(() =>
        {
            // Unregister from clipboard chain on the correct thread
            if (_hwndSource != null && _nextClipboardViewer != IntPtr.Zero)
            {
                ChangeClipboardChain(_hwndSource.Handle, _nextClipboardViewer);
                _nextClipboardViewer = IntPtr.Zero;
            }

            // Dispose HwndSource on the thread it was created
            _hwndSource?.Dispose();
            _hwndSource = null;

            // Shutdown the dispatcher to exit Dispatcher.Run()
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                System.Windows.Threading.DispatcherPriority.Normal);
        });

        // Wait for the thread to finish (with timeout)
        if (_watcherThread.IsAlive)
        {
            _watcherThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void WatcherThreadProc()
    {
        // Create a hidden window for clipboard notifications
        var parameters = new HwndSourceParameters("ClipboardWatcher")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0x800000 // WS_POPUP
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // Register for clipboard notifications
        _nextClipboardViewer = SetClipboardViewer(_hwndSource.Handle);

        // Message loop
        System.Windows.Threading.Dispatcher.Run();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DRAWCLIPBOARD = 0x0308;
        const int WM_CHANGECBCHAIN = 0x030D;

        switch (msg)
        {
            case WM_DRAWCLIPBOARD:
                if (_isEnabled && !_cts.IsCancellationRequested)
                {
                    OnClipboardChanged();
                }

                // Pass to next viewer
                if (_nextClipboardViewer != IntPtr.Zero)
                {
                    SendMessage(_nextClipboardViewer, msg, wParam, lParam);
                }
                handled = true;
                break;

            case WM_CHANGECBCHAIN:
                if (wParam == _nextClipboardViewer)
                {
                    _nextClipboardViewer = lParam;
                }
                else if (_nextClipboardViewer != IntPtr.Zero)
                {
                    SendMessage(_nextClipboardViewer, msg, wParam, lParam);
                }
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText() &&
                !System.Windows.Clipboard.ContainsImage() &&
                !System.Windows.Clipboard.ContainsData(DataFormats.Rtf) &&
                !System.Windows.Clipboard.ContainsData(DataFormats.Html))
            {
                return;
            }

            ClipboardDataMessage? message = null;

            if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image != null)
                {
                    var imageData = BitmapSourceToBytes(image);
                    var hash = ComputeHash(imageData);

                    if (hash != _lastClipboardHash)
                    {
                        _lastClipboardHash = hash;
                        message = new ClipboardDataMessage
                        {
                            ClipboardId = Guid.NewGuid().ToString(),
                            ContentType = ClipboardContentType.Image,
                            Data = imageData,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }
            }
            else if (System.Windows.Clipboard.ContainsData(DataFormats.Rtf))
            {
                var rtf = System.Windows.Clipboard.GetData(DataFormats.Rtf) as string;
                if (!string.IsNullOrEmpty(rtf))
                {
                    var data = System.Text.Encoding.UTF8.GetBytes(rtf);
                    var hash = ComputeHash(data);

                    if (hash != _lastClipboardHash)
                    {
                        _lastClipboardHash = hash;
                        message = new ClipboardDataMessage
                        {
                            ClipboardId = Guid.NewGuid().ToString(),
                            ContentType = ClipboardContentType.Rtf,
                            Data = data,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }
            }
            else if (System.Windows.Clipboard.ContainsData(DataFormats.Html))
            {
                var html = System.Windows.Clipboard.GetData(DataFormats.Html) as string;
                if (!string.IsNullOrEmpty(html))
                {
                    var data = System.Text.Encoding.UTF8.GetBytes(html);
                    var hash = ComputeHash(data);

                    if (hash != _lastClipboardHash)
                    {
                        _lastClipboardHash = hash;
                        message = new ClipboardDataMessage
                        {
                            ClipboardId = Guid.NewGuid().ToString(),
                            ContentType = ClipboardContentType.Html,
                            Data = data,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    var data = System.Text.Encoding.UTF8.GetBytes(text);
                    var hash = ComputeHash(data);

                    if (hash != _lastClipboardHash)
                    {
                        _lastClipboardHash = hash;
                        message = new ClipboardDataMessage
                        {
                            ClipboardId = Guid.NewGuid().ToString(),
                            ContentType = ClipboardContentType.Text,
                            Data = data,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }
            }

            if (message != null)
            {
                _ = _peerManager.BroadcastAsync(MessageType.ClipboardData, message);
            }
        }
        catch
        {
            // Clipboard might be locked by another process
        }
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        if (e.Type != MessageType.ClipboardData || !_isEnabled)
            return;

        try
        {
            var msg = MessagePackSerializer.Deserialize<ClipboardDataMessage>(e.Payload);

            // Update last hash to prevent echo
            _lastClipboardHash = ComputeHash(msg.Data);

            // Set clipboard content
            _hwndSource?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    switch (msg.ContentType)
                    {
                        case ClipboardContentType.Text:
                            var text = System.Text.Encoding.UTF8.GetString(msg.Data);
                            System.Windows.Clipboard.SetText(text);
                            break;

                        case ClipboardContentType.Image:
                            var image = BytesToBitmapSource(msg.Data);
                            if (image != null)
                            {
                                System.Windows.Clipboard.SetImage(image);
                            }
                            break;

                        case ClipboardContentType.Rtf:
                            var rtf = System.Text.Encoding.UTF8.GetString(msg.Data);
                            System.Windows.Clipboard.SetData(DataFormats.Rtf, rtf);
                            break;

                        case ClipboardContentType.Html:
                            var html = System.Text.Encoding.UTF8.GetString(msg.Data);
                            System.Windows.Clipboard.SetData(DataFormats.Html, html);
                            break;
                    }

                    ClipboardReceived?.Invoke(this, new ClipboardReceivedEventArgs
                    {
                        PeerId = e.PeerId!,
                        ContentType = msg.ContentType,
                        DataSize = msg.Data.Length
                    });
                }
                catch
                {
                    // Clipboard might be locked
                }
            });
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private static byte[] BitmapSourceToBytes(BitmapSource source)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource? BytesToBitmapSource(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHash(byte[] data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _peerManager.MessageReceived -= OnMessageReceived;

            // Stop() handles HwndSource cleanup on the correct thread
            Stop();

            _cts.Dispose();
            _disposed = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardViewer(IntPtr hWndNewViewer);

    [DllImport("user32.dll")]
    private static extern bool ChangeClipboardChain(IntPtr hWndRemove, IntPtr hWndNewNext);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

public class ClipboardReceivedEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required ClipboardContentType ContentType { get; init; }
    public required int DataSize { get; init; }
}
