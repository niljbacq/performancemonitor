using System;
using System.Text.RegularExpressions;

namespace TaskManager.Providers;

public sealed class WindowsWifiProvider : IWifiProvider
{
    public WifiDetails GetDetails(string interfaceName)
    {
        string output = CommandRunner.Run("netsh", "wlan show interfaces", 2000);

        var stateMatch = Regex.Match(output, @"^\s*State\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string state = stateMatch.Success ? stateMatch.Groups[1].Value.Trim() : "Disconnected";

        if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
        {
            var ssidMatch = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string ssid = ssidMatch.Success && !string.IsNullOrWhiteSpace(ssidMatch.Groups[1].Value)
                ? ssidMatch.Groups[1].Value.Trim()
                : "Connected";

            var radioMatch = Regex.Match(output, @"^\s*Radio type\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string connectionType = radioMatch.Success ? radioMatch.Groups[1].Value.Trim() : "Unknown";

            var signalMatch = Regex.Match(output, @"^\s*Signal\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string signal = signalMatch.Success ? $"📶 {signalMatch.Groups[1].Value.Trim()}" : "-";

            return new WifiDetails("Connected", ssid, connectionType, signal);
        }

        return new WifiDetails("Disconnected", "Not connected", "Unknown", "-");
    }
}

public sealed class LinuxWifiProvider : IWifiProvider
{
    public WifiDetails GetDetails(string interfaceName)
    {
        string status = "Disconnected";
        string ssid = "Not connected";
        string signalStrength = "-";
        bool detected = false;

        string nmcli = CommandRunner.Run("nmcli", "-t -f active,ssid,signal dev wifi", 2000);
        if (!string.IsNullOrWhiteSpace(nmcli))
        {
            foreach (var line in nmcli.Split('\n'))
            {
                if (!line.StartsWith("yes:", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split(':');
                if (parts.Length >= 3)
                {
                    status = "Connected";
                    ssid = parts[1];
                    signalStrength = $"📶 {parts[2]}%";
                    detected = true;
                    break;
                }
            }
        }

        if (!detected)
        {
            string iw = CommandRunner.Run("iwconfig", interfaceName, 2000);

            var ssidMatch = Regex.Match(iw, @"ESSID:""([^""]+)""");
            if (ssidMatch.Success)
            {
                status = "Connected";
                ssid = ssidMatch.Groups[1].Value;
            }

            var signalMatch = Regex.Match(iw, @"Link Quality=(\d+/\d+)");
            signalStrength = signalMatch.Success ? $"📶 {signalMatch.Groups[1].Value}" : "-";
        }

        string link = CommandRunner.Run("iw", $"dev {interfaceName} link", 2000);
        var freqMatch = Regex.Match(link, @"freq:\s*(\d+)");
        string connectionType = freqMatch.Success && int.TryParse(freqMatch.Groups[1].Value, out int freq)
            ? (freq > 5000 ? "802.11ac/ax (5GHz)" : "802.11n/ax (2.4GHz)")
            : "802.11 Wireless";

        return new WifiDetails(status, ssid, connectionType, signalStrength);
    }
}