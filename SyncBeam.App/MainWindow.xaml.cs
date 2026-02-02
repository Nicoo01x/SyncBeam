using System.IO;
using System.Text.Json;
using System.Windows;
using MessagePack;
using Microsoft.Web.WebView2.Core;
using SyncBeam.Clipboard;
using SyncBeam.P2P;
using SyncBeam.P2P.Transport;
using SyncBeam.Streams;

namespace SyncBeam.App;

public partial class MainWindow : Window
{
    private PeerManager? _peerManager;
    private FileTransferEngine? _transferEngine;
    private ClipboardWatcher? _clipboardWatcher;
    private OutboxWatcher? _outboxWatcher;

    private readonly string _syncBeamPath;

    public MainWindow()
    {
        InitializeComponent();

        _syncBeamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SyncBeam");

        // Ensure SyncBeam directory exists
        Directory.CreateDirectory(_syncBeamPath);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
        InitializeBackend();
    }

    private async Task InitializeWebViewAsync()
    {
        await WebView.EnsureCoreWebView2Async();

        // Get the wwwroot directory path
        var wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");

        if (Directory.Exists(wwwrootPath))
        {
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "syncbeam.local",
                wwwrootPath,
                CoreWebView2HostResourceAccessKind.Allow);

            WebView.CoreWebView2.Navigate("https://syncbeam.local/index.html");
        }
        else
        {
            WebView.NavigateToString(GetFallbackHtml());
        }

        WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
            window.SyncBeam = {
                send: (action, data) => {
                    window.chrome.webview.postMessage(JSON.stringify({ action, data }));
                }
            };
        ");
    }

    private void InitializeBackend()
    {
        // Initialize PeerManager (auto-discovers and auto-connects to LAN peers)
        _peerManager = new PeerManager();

        _peerManager.PeerDiscovered += (_, e) =>
        {
            SendToUI("peerDiscovered", new
            {
                peerId = e.PeerId,
                endpoint = e.Endpoint.ToString()
            });
        };

        _peerManager.PeerConnected += (_, e) =>
        {
            SendToUI("peerConnected", new
            {
                peerId = e.PeerId,
                isIncoming = e.Peer.IsIncoming
            });
        };

        _peerManager.PeerDisconnected += (_, e) =>
        {
            SendToUI("peerDisconnected", new { peerId = e.PeerId });
        };

        _peerManager.NetworkDeviceDiscovered += (_, e) =>
        {
            SendToUI("networkDevice", new
            {
                ip = e.Device.IpAddress.ToString(),
                hostname = e.Device.Hostname,
                hasSyncBeam = e.Device.HasSyncBeam,
                peerId = e.Device.SyncBeamPeerId,
                isConnected = e.Device.IsConnected,
                deviceType = e.Device.DeviceType.ToString()
            });
        };

        _peerManager.NetworkScanCompleted += (_, _) =>
        {
            SendToUI("networkScanCompleted", new { });
        };

        _peerManager.MessageReceived += OnPeerMessage;

        // Initialize FileTransferEngine
        _transferEngine = new FileTransferEngine(_peerManager, _syncBeamPath);

        _transferEngine.FileAnnounced += (_, e) =>
        {
            SendToUI("fileAnnounced", new
            {
                peerId = e.PeerId,
                transferId = e.TransferId,
                fileName = e.FileName,
                fileSize = e.FileSize,
                mimeType = e.MimeType
            });
        };

        _transferEngine.TransferProgress += (_, e) =>
        {
            SendToUI("transferProgress", new
            {
                transferId = e.TransferId,
                fileName = e.FileName,
                progress = e.Progress,
                bytesTransferred = e.BytesTransferred,
                totalBytes = e.TotalBytes
            });
        };

        _transferEngine.TransferCompleted += (_, e) =>
        {
            SendToUI("transferCompleted", new
            {
                transferId = e.TransferId,
                fileName = e.FileName,
                success = e.Success,
                filePath = e.FilePath,
                errorMessage = e.ErrorMessage
            });
        };

        // Initialize ClipboardWatcher
        _clipboardWatcher = new ClipboardWatcher(_peerManager);

        _clipboardWatcher.ClipboardReceived += (_, e) =>
        {
            SendToUI("clipboardReceived", new
            {
                peerId = e.PeerId,
                contentType = e.ContentType.ToString(),
                dataSize = e.DataSize
            });
        };

        // Initialize OutboxWatcher
        var outboxPath = Path.Combine(_syncBeamPath, "outbox");
        _outboxWatcher = new OutboxWatcher(_transferEngine, outboxPath);

        _outboxWatcher.FileDetected += (_, e) =>
        {
            SendToUI("fileDetected", new
            {
                filePath = e.FilePath,
                fileName = Path.GetFileName(e.FilePath)
            });
        };

        // Start everything
        _peerManager.Start();
        _clipboardWatcher.Start();
        _outboxWatcher.Start();

        // Send initial state
        SendToUI("initialized", new
        {
            localPeerId = _peerManager.LocalPeerId,
            listenPort = _peerManager.ListenPort,
            inboxPath = Path.Combine(_syncBeamPath, "inbox"),
            outboxPath = outboxPath
        });
    }

    private void OnPeerMessage(object? sender, MessageReceivedEventArgs e)
    {
        switch (e.Type)
        {
            case MessageType.ClipboardData:
                var clipMsg = MessagePackSerializer.Deserialize<ClipboardDataMessage>(e.Payload);
                SendToUI("clipboardData", new
                {
                    peerId = e.PeerId,
                    contentType = clipMsg.ContentType.ToString(),
                    size = clipMsg.Data.Length
                });
                break;

            case MessageType.FileAnnounce:
                var announceMsg = MessagePackSerializer.Deserialize<FileAnnounceMessage>(e.Payload);
                _transferEngine?.PrepareIncomingTransfer(e.PeerId!, announceMsg);
                SendToUI("fileAnnounced", new
                {
                    peerId = e.PeerId,
                    transferId = announceMsg.TransferId,
                    fileName = announceMsg.FileName,
                    fileSize = announceMsg.FileSize
                });
                break;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            System.Diagnostics.Debug.WriteLine($"WebMessage received: {json}");
            var message = JsonDocument.Parse(json);
            var action = message.RootElement.GetProperty("action").GetString();
            var data = message.RootElement.TryGetProperty("data", out var dataElement)
                ? dataElement
                : default;

            System.Diagnostics.Debug.WriteLine($"Handling action: {action}");
            HandleUIMessage(action, data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebMessage error: {ex.Message}");
        }
    }

    private async void HandleUIMessage(string? action, JsonElement data)
    {
        if (_peerManager == null) return;

        switch (action)
        {
            case "connect":
                var peerId = data.GetProperty("peerId").GetString();
                if (peerId != null)
                    await _peerManager.ConnectToPeerAsync(peerId);
                break;

            case "refresh":
                _peerManager.RefreshDiscovery();
                break;

            case "scanNetwork":
                _peerManager.ScanNetwork();
                break;

            case "acceptFile":
                var acceptPeerId = data.GetProperty("peerId").GetString();
                var acceptTransferId = data.GetProperty("transferId").GetString();
                if (acceptPeerId != null && acceptTransferId != null)
                    await _transferEngine!.AcceptFileAsync(acceptPeerId, acceptTransferId);
                break;

            case "cancelTransfer":
                var cancelPeerId = data.GetProperty("peerId").GetString();
                var cancelTransferId = data.GetProperty("transferId").GetString();
                if (cancelPeerId != null && cancelTransferId != null)
                    await _transferEngine!.CancelTransferAsync(cancelPeerId, cancelTransferId);
                break;

            case "setClipboardSync":
                var enabled = data.GetProperty("enabled").GetBoolean();
                if (_clipboardWatcher != null)
                    _clipboardWatcher.IsEnabled = enabled;
                break;

            case "getState":
                SendCurrentState();
                break;

            case "openInbox":
                var inboxPath = Path.Combine(_syncBeamPath, "inbox");
                System.Diagnostics.Process.Start("explorer.exe", inboxPath);
                break;

            case "openOutbox":
                var outboxPath = Path.Combine(_syncBeamPath, "outbox");
                System.Diagnostics.Process.Start("explorer.exe", outboxPath);
                break;
        }
    }

    private void SendCurrentState()
    {
        if (_peerManager == null) return;

        var peers = _peerManager.ConnectedPeers.Select(p => new
        {
            peerId = p.Key,
            isIncoming = p.Value.IsIncoming
        }).ToList();

        SendToUI("state", new
        {
            localPeerId = _peerManager.LocalPeerId,
            listenPort = _peerManager.ListenPort,
            connectedPeers = peers,
            clipboardSyncEnabled = _clipboardWatcher?.IsEnabled ?? false
        });
    }

    private void SendToUI(string eventName, object data)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(new { @event = eventName, data });
                await WebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.dispatchEvent(new CustomEvent('syncbeam', {{ detail: {json} }}))");
            }
            catch
            {
                // WebView might not be ready
            }
        });
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _outboxWatcher?.Dispose();
        _clipboardWatcher?.Dispose();
        _transferEngine?.Dispose();
        _peerManager?.Dispose();
    }

    private static string GetFallbackHtml()
    {
        return """
        <!DOCTYPE html>
        <html>
        <head>
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&display=swap');
                body {
                    font-family: 'Outfit', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                    background: #0D1117;
                    color: #E6EDF3;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    height: 100vh;
                    margin: 0;
                }
                .loader {
                    text-align: center;
                }
                .spinner {
                    width: 50px;
                    height: 50px;
                    border: 3px solid #30363D;
                    border-top-color: #58A6FF;
                    border-radius: 50%;
                    animation: spin 1s linear infinite;
                    margin: 0 auto 20px;
                }
                @keyframes spin {
                    to { transform: rotate(360deg); }
                }
                h2 { font-weight: 500; margin-bottom: 8px; }
                p { color: #8B949E; }
            </style>
        </head>
        <body>
            <div class="loader">
                <div class="spinner"></div>
                <h2>Loading SyncBeam...</h2>
                <p>Initializing peer-to-peer network</p>
            </div>
        </body>
        </html>
        """;
    }
}
