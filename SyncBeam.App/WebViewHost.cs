using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SyncBeam.App;

/// <summary>
/// Host object exposed to JavaScript via WebView2.
/// Provides a bridge for two-way communication between the UI and the backend.
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class WebViewHost
{
    public event EventHandler<WebViewMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Called from JavaScript to send messages to C#.
    /// </summary>
    public void PostMessage(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var action = doc.RootElement.GetProperty("action").GetString() ?? "";
            var data = doc.RootElement.TryGetProperty("data", out var dataElement)
                ? dataElement
                : default;

            MessageReceived?.Invoke(this, new WebViewMessageEventArgs
            {
                Action = action,
                Data = data
            });
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    /// <summary>
    /// Get the current version of SyncBeam.
    /// </summary>
    public string GetVersion()
    {
        return "1.0.0";
    }

    /// <summary>
    /// Get the application data path.
    /// </summary>
    public string GetDataPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SyncBeam");
    }
}

public class WebViewMessageEventArgs : EventArgs
{
    public required string Action { get; init; }
    public JsonElement Data { get; init; }
}
