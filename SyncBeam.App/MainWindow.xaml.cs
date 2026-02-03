using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using MessagePack;
using Microsoft.Web.WebView2.Core;
using SyncBeam.Clipboard;
using SyncBeam.P2P;
using SyncBeam.P2P.Network;
using SyncBeam.P2P.Transport;
using SyncBeam.Streams;

namespace SyncBeam.App;

public partial class MainWindow : Window
{
    private PeerManager? _peerManager;
    private FileTransferEngine? _transferEngine;
    private ClipboardWatcher? _clipboardWatcher;
    private OutboxWatcher? _outboxWatcher;
    private UpdateChecker? _updateChecker;
    private AppSettings _settings;

    private readonly string _syncBeamPath;

    public MainWindow()
    {
        InitializeComponent();

        _syncBeamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SyncBeam");

        // Ensure SyncBeam directory exists
        Directory.CreateDirectory(_syncBeamPath);

        // Load settings
        _settings = AppSettings.Load();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Check if we were launched to configure firewall
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--configure-firewall"))
        {
            await ConfigureFirewallAndExitAsync();
            return;
        }

        await InitializeWebViewAsync();
        InitializeBackend();
        await CheckForUpdatesAsync();

        // Check if firewall needs configuration
        await CheckFirewallOnStartupAsync();
    }

    private async Task ConfigureFirewallAndExitAsync()
    {
        // This runs when app is elevated to configure firewall
        try
        {
            var result = SyncBeam.P2P.Network.FirewallManager.ConfigureRules(_settings.ListenPort, _settings.ListenPort);
            if (result.Success)
            {
                MessageBox.Show(
                    "Firewall configured successfully!\n\nYou can now run SyncBeam normally.",
                    "SyncBeam",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Failed to configure firewall:\n{result.Message}",
                    "SyncBeam",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error configuring firewall:\n{ex.Message}",
                "SyncBeam",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Application.Current.Shutdown();
    }

    private async Task CheckFirewallOnStartupAsync()
    {
        // Wait a bit for the network setup to run first
        await Task.Delay(2000);

        // Check if firewall needs configuration
        if (!SyncBeam.P2P.Network.FirewallManager.AreRulesConfigured() &&
            !SyncBeam.P2P.Network.FirewallManager.IsRunningAsAdmin())
        {
            // Send event to UI to ask user
            SendToUI("firewallSetupRequired", new
            {
                message = "SyncBeam needs to configure Windows Firewall to allow connections from other devices.",
                port = _settings.ListenPort
            });
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateChecker = new UpdateChecker();
        _updateChecker.UpdateAvailable += (_, e) =>
        {
            SendToUI("updateAvailable", new
            {
                currentVersion = e.CurrentVersion,
                latestVersion = e.LatestVersion,
                releaseNotes = e.ReleaseNotes,
                downloadUrl = e.DownloadUrl,
                releaseUrl = e.ReleaseUrl
            });
        };

        await _updateChecker.CheckForUpdatesAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        await WebView.EnsureCoreWebView2Async();

        // Disable developer tools (F12)
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

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
        // Initialize PeerManager with configured port
        _peerManager = new PeerManager(_settings.ListenPort);

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

        _peerManager.PeerConnectionFailed += (_, e) =>
        {
            SendToUI("peerConnectionFailed", new
            {
                peerId = e.PeerId,
                errorMessage = e.ErrorMessage
            });
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

        _peerManager.NetworkStatusChanged += (_, e) =>
        {
            SendToUI("networkStatus", new
            {
                component = e.Component.ToString(),
                status = e.Status.ToString(),
                message = e.Message
            });
        };

        _peerManager.SetupProgress += (_, e) =>
        {
            SendToUI("setupProgress", new
            {
                message = e.Message,
                percentComplete = e.PercentComplete
            });
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

        // Send initial state with network status
        var networkStatus = _peerManager.GetNetworkStatus();
        SendToUI("initialized", new
        {
            localPeerId = _peerManager.LocalPeerId,
            listenPort = _peerManager.ListenPort,
            inboxPath = Path.Combine(_syncBeamPath, "inbox"),
            outboxPath = outboxPath,
            firewallConfigured = networkStatus.FirewallConfigured,
            upnpAvailable = networkStatus.UpnpAvailable
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

            case "connectToIp":
                var ip = data.GetProperty("ip").GetString();
                if (ip != null)
                    await _peerManager.ConnectToIpAsync(ip);
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

            case "openUrl":
                var url = data.GetProperty("url").GetString();
                if (!string.IsNullOrEmpty(url))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                break;

            case "configureFirewall":
                await ConfigureFirewallAsync();
                break;

            case "configureUpnp":
                await ConfigureUpnpAsync();
                break;

            case "runDiagnostics":
                await RunDiagnosticsAsync();
                break;

            case "getNetworkStatus":
                SendNetworkStatus();
                break;

            case "checkPeerConnectivity":
                var checkIp = data.GetProperty("ip").GetString();
                var checkPort = data.TryGetProperty("port", out var portElement)
                    ? portElement.GetInt32()
                    : _peerManager?.ListenPort ?? 42420;
                if (!string.IsNullOrEmpty(checkIp))
                    await CheckPeerConnectivityAsync(checkIp, checkPort);
                break;

            case "savePort":
                var newPort = data.GetProperty("port").GetInt32();
                SavePortSetting(newPort);
                break;

            case "getSettings":
                SendCurrentSettings();
                break;

            case "requestFirewallSetup":
                RequestFirewallElevation();
                break;
        }
    }

    private void RequestFirewallElevation()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                SendToUI("firewallSetupResult", new { success = false, message = "Could not find application path" });
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--configure-firewall",
                UseShellExecute = true,
                Verb = "runas" // This triggers UAC elevation
            };

            System.Diagnostics.Process.Start(startInfo);

            // Close this instance - the elevated one will configure firewall
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC prompt
            SendToUI("firewallSetupResult", new
            {
                success = false,
                message = "Administrator permission was denied. You can configure firewall manually in Settings."
            });
        }
        catch (Exception ex)
        {
            SendToUI("firewallSetupResult", new { success = false, message = ex.Message });
        }
    }

    private void SavePortSetting(int newPort)
    {
        if (newPort < 1024 || newPort > 65535)
        {
            SendToUI("portSaveResult", new
            {
                success = false,
                message = "Port must be between 1024 and 65535"
            });
            return;
        }

        var currentPort = _settings.ListenPort;
        _settings.ListenPort = newPort;
        _settings.Save();

        var needsRestart = newPort != currentPort;

        SendToUI("portSaveResult", new
        {
            success = true,
            port = newPort,
            needsRestart,
            message = needsRestart
                ? "Port saved. Restart SyncBeam for changes to take effect."
                : "Port saved."
        });
    }

    private void SendCurrentSettings()
    {
        SendToUI("settings", new
        {
            listenPort = _settings.ListenPort,
            currentListenPort = _peerManager?.ListenPort ?? _settings.ListenPort
        });
    }

    private async Task ConfigureFirewallAsync()
    {
        if (_peerManager == null) return;

        var result = await _peerManager.ConfigureFirewallAsync();
        SendToUI("firewallConfigResult", new
        {
            success = result.Success,
            requiresElevation = result.RequiresElevation,
            message = result.Message
        });

        if (result.RequiresElevation)
        {
            // Ask user if they want to elevate
            SendToUI("firewallElevationRequired", new
            {
                message = "Administrator privileges are required to configure the firewall. Would you like to restart SyncBeam with elevated permissions?"
            });
        }
    }

    private async Task ConfigureUpnpAsync()
    {
        if (_peerManager == null) return;

        var success = await _peerManager.ConfigureUpnpAsync();
        var status = _peerManager.GetNetworkStatus();

        SendToUI("upnpConfigResult", new
        {
            success,
            externalIp = status.ExternalIp?.ToString(),
            portMapped = status.PortMapped,
            message = success ? $"Port {status.ListenPort} mapped successfully" : "Failed to configure UPnP"
        });
    }

    private async Task RunDiagnosticsAsync()
    {
        if (_peerManager == null) return;

        SendToUI("diagnosticsStarted", new { });

        var report = await _peerManager.RunDiagnosticsAsync();

        SendToUI("diagnosticResult", new
        {
            portAvailable = report.PortAvailable,
            internetConnected = report.InternetConnectivity.IsConnected,
            internetLatency = report.InternetConnectivity.Latency,
            dnsWorking = report.InternetConnectivity.DnsWorking,
            stunSuccess = report.StunResult.Success,
            publicEndpoint = report.StunResult.PublicEndpoint?.ToString(),
            natType = report.StunResult.NatType.ToString(),
            upnpFound = report.UpnpResult.DeviceFound,
            upnpExternalIp = report.UpnpResult.ExternalIpAddress?.ToString(),
            gatewayAddress = report.UpnpResult.GatewayAddress?.ToString(),
            firewallConfigured = report.FirewallStatus.RulesConfigured,
            firewallEnabled = report.FirewallStatus.FirewallEnabled,
            isAdmin = report.FirewallStatus.IsAdmin,
            recommendations = report.Recommendations,
            interfaces = report.LocalInterfaces.Select(i => new
            {
                name = i.Name,
                description = i.Description,
                type = i.Type,
                isUp = i.IsUp,
                addresses = i.IPv4Addresses.Select(a => a.ToString()).ToList(),
                gateways = i.GatewayAddresses.Select(a => a.ToString()).ToList()
            }).ToList(),
            duration = report.Duration.TotalMilliseconds
        });
    }

    private void SendNetworkStatus()
    {
        if (_peerManager == null) return;

        var status = _peerManager.GetNetworkStatus();
        SendToUI("networkStatusFull", new
        {
            firewallConfigured = status.FirewallConfigured,
            upnpAvailable = status.UpnpAvailable,
            portMapped = status.PortMapped,
            natType = status.NatType.ToString(),
            publicEndpoint = status.PublicEndpoint?.ToString(),
            externalIp = status.ExternalIp?.ToString(),
            localIp = status.LocalIp?.ToString(),
            gatewayIp = status.GatewayIp?.ToString(),
            listenPort = status.ListenPort
        });
    }

    private async Task CheckPeerConnectivityAsync(string ipAddress, int port)
    {
        if (_peerManager == null) return;

        if (!IPAddress.TryParse(ipAddress, out var ip))
        {
            SendToUI("peerConnectivityResult", new
            {
                success = false,
                diagnosis = "Invalid IP address"
            });
            return;
        }

        var endpoint = new IPEndPoint(ip, port);
        var result = await _peerManager.CheckPeerConnectivityAsync(endpoint);

        SendToUI("peerConnectivityResult", new
        {
            endpoint = endpoint.ToString(),
            pingSuccessful = result.PingSuccessful,
            pingLatency = result.PingLatency,
            tcpPortOpen = result.TcpPortOpen,
            udpReachable = result.UdpReachable,
            diagnosis = result.Diagnosis
        });
    }

    private void SendCurrentState()
    {
        if (_peerManager == null) return;

        var peers = _peerManager.ConnectedPeers.Select(p => new
        {
            peerId = p.Key,
            isIncoming = p.Value.IsIncoming
        }).ToList();

        var networkStatus = _peerManager.GetNetworkStatus();

        SendToUI("state", new
        {
            localPeerId = _peerManager.LocalPeerId,
            listenPort = _peerManager.ListenPort,
            connectedPeers = peers,
            clipboardSyncEnabled = _clipboardWatcher?.IsEnabled ?? false,
            networkSetupComplete = _peerManager.IsNetworkSetupComplete,
            firewallConfigured = networkStatus.FirewallConfigured,
            upnpAvailable = networkStatus.UpnpAvailable,
            portMapped = networkStatus.PortMapped,
            natType = networkStatus.NatType.ToString(),
            publicEndpoint = networkStatus.PublicEndpoint?.ToString(),
            externalIp = networkStatus.ExternalIp?.ToString()
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
