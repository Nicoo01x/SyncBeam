using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SyncBeam.P2P.Network;

/// <summary>
/// Manages Windows Firewall rules for SyncBeam.
/// Uses netsh command-line tool for firewall configuration.
/// </summary>
public static class FirewallManager
{
    private const string RuleNameTcp = "SyncBeam TCP";
    private const string RuleNameUdp = "SyncBeam UDP";
    private const string RuleNameTcpOut = "SyncBeam TCP Out";
    private const string RuleNameUdpOut = "SyncBeam UDP Out";

    /// <summary>
    /// Checks if the SyncBeam firewall rules are already configured.
    /// </summary>
    public static bool AreRulesConfigured()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true; // Non-Windows platforms don't need this

        try
        {
            // Check if rules exist using netsh
            var tcpResult = RunNetsh($"advfirewall firewall show rule name=\"{RuleNameTcp}\"");
            var udpResult = RunNetsh($"advfirewall firewall show rule name=\"{RuleNameUdp}\"");

            return tcpResult.Contains(RuleNameTcp) && udpResult.Contains(RuleNameUdp);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FirewallManager] Error checking rules: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if the current process has administrator privileges.
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if elevation is required to configure firewall rules.
    /// </summary>
    public static bool RequiresElevation()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        return !AreRulesConfigured() && !IsRunningAsAdmin();
    }

    /// <summary>
    /// Configures Windows Firewall rules for SyncBeam.
    /// Requires administrator privileges.
    /// </summary>
    /// <param name="tcpPort">The TCP port to allow</param>
    /// <param name="udpPort">The UDP port to allow</param>
    /// <returns>True if rules were configured successfully</returns>
    public static FirewallConfigResult ConfigureRules(int tcpPort, int udpPort)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new FirewallConfigResult { Success = true, Message = "Not running on Windows" };

        if (AreRulesConfigured())
            return new FirewallConfigResult { Success = true, Message = "Firewall rules already configured" };

        if (!IsRunningAsAdmin())
            return new FirewallConfigResult
            {
                Success = false,
                RequiresElevation = true,
                Message = "Administrator privileges required to configure firewall"
            };

        try
        {
            var appPath = Process.GetCurrentProcess().MainModule?.FileName ?? "SyncBeam.App.exe";
            var errors = new List<string>();

            // Create inbound TCP rule
            var tcpIn = RunNetsh(
                $"advfirewall firewall add rule " +
                $"name=\"{RuleNameTcp}\" " +
                $"dir=in action=allow " +
                $"protocol=tcp localport={tcpPort} " +
                $"program=\"{appPath}\" " +
                $"enable=yes profile=private,public");

            if (!tcpIn.Contains("Ok"))
                errors.Add($"TCP inbound: {tcpIn}");

            // Create inbound UDP rule
            var udpIn = RunNetsh(
                $"advfirewall firewall add rule " +
                $"name=\"{RuleNameUdp}\" " +
                $"dir=in action=allow " +
                $"protocol=udp localport={udpPort} " +
                $"program=\"{appPath}\" " +
                $"enable=yes profile=private,public");

            if (!udpIn.Contains("Ok"))
                errors.Add($"UDP inbound: {udpIn}");

            // Create outbound TCP rule
            var tcpOut = RunNetsh(
                $"advfirewall firewall add rule " +
                $"name=\"{RuleNameTcpOut}\" " +
                $"dir=out action=allow " +
                $"protocol=tcp " +
                $"program=\"{appPath}\" " +
                $"enable=yes profile=private,public");

            if (!tcpOut.Contains("Ok"))
                errors.Add($"TCP outbound: {tcpOut}");

            // Create outbound UDP rule
            var udpOut = RunNetsh(
                $"advfirewall firewall add rule " +
                $"name=\"{RuleNameUdpOut}\" " +
                $"dir=out action=allow " +
                $"protocol=udp " +
                $"program=\"{appPath}\" " +
                $"enable=yes profile=private,public");

            if (!udpOut.Contains("Ok"))
                errors.Add($"UDP outbound: {udpOut}");

            if (errors.Count > 0)
            {
                return new FirewallConfigResult
                {
                    Success = false,
                    Message = $"Some rules failed to configure: {string.Join("; ", errors)}"
                };
            }

            return new FirewallConfigResult
            {
                Success = true,
                Message = "Firewall rules configured successfully"
            };
        }
        catch (Exception ex)
        {
            return new FirewallConfigResult
            {
                Success = false,
                Message = $"Failed to configure firewall: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Removes all SyncBeam firewall rules.
    /// </summary>
    public static bool RemoveRules()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

        if (!IsRunningAsAdmin())
            return false;

        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleNameTcp}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleNameUdp}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleNameTcpOut}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleNameUdpOut}\"");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Requests elevation by restarting the application as administrator.
    /// </summary>
    /// <param name="args">Arguments to pass to the elevated process</param>
    public static void RequestElevation(string? args = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args ?? "--configure-firewall",
                UseShellExecute = true,
                Verb = "runas" // This triggers UAC elevation
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FirewallManager] Elevation request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets detailed firewall status information.
    /// </summary>
    public static FirewallStatus GetStatus()
    {
        var status = new FirewallStatus();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            status.IsWindows = false;
            status.Message = "Not running on Windows";
            return status;
        }

        status.IsWindows = true;
        status.RulesConfigured = AreRulesConfigured();
        status.IsAdmin = IsRunningAsAdmin();

        try
        {
            // Check if firewall service is running
            var profileResult = RunNetsh("advfirewall show currentprofile state");
            status.FirewallEnabled = profileResult.Contains("ON");

            // Get current profile
            if (profileResult.Contains("Domain"))
                status.CurrentProfile = "Domain";
            else if (profileResult.Contains("Private"))
                status.CurrentProfile = "Private";
            else if (profileResult.Contains("Public"))
                status.CurrentProfile = "Public";

            if (status.RulesConfigured)
                status.Message = "Firewall configured correctly";
            else if (status.IsAdmin)
                status.Message = "Firewall rules need to be configured";
            else
                status.Message = "Administrator privileges required to configure firewall";
        }
        catch (Exception ex)
        {
            status.Message = $"Error checking firewall status: {ex.Message}";
        }

        return status;
    }

    private static string RunNetsh(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return string.Empty;

            // Read output asynchronously to avoid deadlock when buffers fill
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return "Error: Command timed out";
            }

            // Now safe to get results since process has exited
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();

            return output + error;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

/// <summary>
/// Result of a firewall configuration attempt.
/// </summary>
public class FirewallConfigResult
{
    public bool Success { get; init; }
    public bool RequiresElevation { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Current firewall status information.
/// </summary>
public class FirewallStatus
{
    public bool IsWindows { get; set; }
    public bool FirewallEnabled { get; set; }
    public bool RulesConfigured { get; set; }
    public bool IsAdmin { get; set; }
    public string CurrentProfile { get; set; } = "Unknown";
    public string Message { get; set; } = string.Empty;
}
