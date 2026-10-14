using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
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

    public WifiSnapshot? GetSnapshot()
    {
        try
        {
            NetworkInterface? wifi = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic => (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                        nic.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ||
                                        nic.Name.StartsWith("wlp", StringComparison.OrdinalIgnoreCase) ||
                                        nic.Name.StartsWith("wlx", StringComparison.OrdinalIgnoreCase)) &&
                                       nic.OperationalStatus == OperationalStatus.Up);

            if (wifi == null)
                return new WifiSnapshot("Wi-Fi", "Disconnected", "Not connected", "-", "-", "-", "-", null, null);

            string adapterName = string.IsNullOrWhiteSpace(wifi.Description) ? wifi.Name : wifi.Description;

            var ipProps = wifi.GetIPProperties();
            string ipv4 = IpOf(ipProps, System.Net.Sockets.AddressFamily.InterNetwork);
            string ipv6 = IpOf(ipProps, System.Net.Sockets.AddressFamily.InterNetworkV6);

            var stats = wifi.GetIPv4Statistics();
            long sent = stats.BytesSent;
            long received = stats.BytesReceived;

            var details = GetDetails(wifi.Name);

            string? gateway = FirstGateway(ipProps);
            string? netmask = FirstNetmask(ipProps);
            var (dns1, dns2) = ReadDns(resolvConfPath: "/etc/resolv.conf");
            string? dhcpServer = ReadDhcpServer(wifi.Name);

            string? wifiStandard = null;
            string? signalPercent = null;
            string? bssid = null;
            if (details.Status == "Connected")
            {
                // signal "📶 78%" -> percent; iw dev link has "signal: -45 dBm"
                var sig = Regex.Match(details.SignalStrength, @"\d+");
                if (sig.Success)
                {
                    if (details.SignalStrength.Contains('%')) signalPercent = details.SignalStrength;
                    else if (int.TryParse(sig.Value, out int dBm))
                        signalPercent = $"📶 {PercentFromDbm(dBm)}%";
                }
                if (signalPercent == null) signalPercent = details.SignalStrength;

                string linkOut = CommandRunner.Run("iw", $"dev {wifi.Name} link", 2000);

                var ssidMatch = Regex.Match(linkOut, @"SSID:\s*(.+)");
                if (ssidMatch.Success) details = new WifiDetails(details.Status, ssidMatch.Groups[1].Value.Trim(), details.ConnectionType, signalPercent);

                bssid = ReadBssid(wifi.Name);

                var freqM = Regex.Match(linkOut, @"freq:\s*(\d+)");
                if (freqM.Success && int.TryParse(freqM.Groups[1].Value, out int f))
                {
                    if (f >= 5925) wifiStandard = "Wi-Fi 6E / 7 (6 GHz)";
                    else if (f >= 4900) wifiStandard = "Wi-Fi 5/6 (5 GHz)";
                    else wifiStandard = "Wi-Fi 4/5 (2.4 GHz)";
                }
            }

            return new WifiSnapshot(
                adapterName, details.Status, details.Ssid, details.ConnectionType, details.SignalStrength,
                ipv4, ipv6, sent, received)
            {
                WifiStandard = wifiStandard,
                SignalPercentText = signalPercent,
                Bssid = bssid,
                DhcpServer = dhcpServer,
                Gateway = gateway,
                Dns1 = dns1,
                Dns2 = dns2,
                Netmask = netmask
            };
        }
        catch
        {
            return null;
        }
    }

    private static string IpOf(IPInterfaceProperties props, System.Net.Sockets.AddressFamily family)
    {
        var addr = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == family);
        return addr?.Address.ToString() ?? "-";
    }

    private static string? FirstGateway(IPInterfaceProperties props)
        => props.GatewayAddresses.Count > 0 ? props.GatewayAddresses[0].Address?.ToString() : null;

    private static string? FirstNetmask(IPInterfaceProperties props)
        => props.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
            .IPv4Mask?.ToString();

    private static (string?, string?) ReadDns(string resolvConfPath)
    {
        string dns1 = "-", dns2 = "-";
        try
        {
            if (File.Exists(resolvConfPath))
            {
                var v = File.ReadAllLines(resolvConfPath)
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("nameserver ", StringComparison.Ordinal))
                    .Select(l => l["nameserver ".Length..].Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (v.Count > 0) dns1 = v[0];
                if (v.Count > 1) dns2 = v[1];
            }
        }
        catch { }
        return (dns1, dns2);
    }

    private static string? ReadDhcpServer(string iface)
    {
        string route = CommandRunner.Run("ip", "route", 2000);
        string? server = null;
        foreach (string line in route.Split('\n'))
        {
            if (!line.Contains(iface, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("default", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"via\s+([0-9.]+)");
                if (m.Success) server = m.Groups[1].Value;
            }
        }
        // The DHCP server itself is often the "dhcp server" option, which `ip route` does not show.
        // Best effort: the gateway we just found.
        return server;
    }

    private static string? ReadBssid(string iface)
    {
        string scan = CommandRunner.Run("iw", $"dev {iface} link", 2000);
        var m = Regex.Match(scan, @"Connected to\s+([0-9a-fA-F:]{17})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int PercentFromDbm(int dBm)
    {
        // Standard mapping: -50 dBm => ~100%, -100 dBm => ~0%.
        int pct = (int)Math.Round((dBm + 100) * 2.0);
        return Math.Clamp(pct, 0, 100);
    }
}