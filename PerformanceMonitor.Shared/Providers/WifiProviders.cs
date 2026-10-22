using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TaskManager.Providers;

public sealed class WindowsWifiProvider : IWifiProvider
{
    public WifiDetails GetDetails(string interfaceName)
    {
        string output = CommandRunner.Run("netsh", "wlan show interfaces", 2000);

        // netsh labels are localised, but the values are not. Pair every
        // "label: value" line and identify fields by their value shape.
        var pairs = new List<(string Label, string Value)>();
        foreach (string line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            pairs.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        int bssidIndex = pairs.FindIndex(p =>
            Regex.IsMatch(p.Value, @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"));

        string? ssid = null;
        if (bssidIndex > 0)
        {
            string candidate = pairs[bssidIndex - 1].Value;
            if (candidate.Length > 0) ssid = candidate;
        }

        string? signal = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^\d{1,3}\s*%$")).Value;
        string? radio = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^802\.11")).Value;

        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name == interfaceName);
        bool connected = bssidIndex >= 0
                         || (nic?.OperationalStatus == OperationalStatus.Up && ssid != null);

        if (!connected)
            return new WifiDetails("Disconnected", "Not connected", radio ?? "-", "-");

        return new WifiDetails(
            "Connected",
            ssid ?? "Connected",
            radio ?? "-",
            signal != null ? $"📶 {signal}" : "-");
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

                // nmcli -t escapes ':' as '\:' — find the first and last unescaped colon
                int firstColon = line.IndexOf(':');
                int lastColon = line.LastIndexOf(':');
                if (firstColon > 0 && lastColon > firstColon)
                {
                    string ssidPart = line.Substring(firstColon + 1, lastColon - firstColon - 1).Replace("\\:", ":");
                    string signalPart = line[(lastColon + 1)..];
                    status = "Connected";
                    ssid = ssidPart;
                    signalStrength = $"📶 {signalPart}%";
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
        string? iface = FindWifiIface();
        if (iface == null)
            return new WifiSnapshot("Wi-Fi", "Disconnected", "No wireless iface", "-", "-", "-", "-", null, null);

        string link = CommandRunner.Run("iw", $"dev {iface} link", 2000);
        bool connected = link.Contains("Connected to", StringComparison.OrdinalIgnoreCase);

        string? ssid = Match(link, @"SSID:\s*(.+)");
        string? bssid = Match(link, @"Connected to\s+([0-9a-fA-F:]{17})");
        string? signalDbm = Match(link, @"signal:\s*(-?\d+)\s*dBm");
        string? freqText = Match(link, @"freq:\s*(\d+)");
        string? rxBitrate = Match(link, @"rx bitrate:\s*(.+)");

        if (ssid == null)
        {
            string id = CommandRunner.Run("iwgetid", $"{iface} -r", 1500);
            if (!string.IsNullOrWhiteSpace(id)) ssid = id.Trim();
        }

        int.TryParse(signalDbm, out int dbm);
        int.TryParse(freqText, out int freqMhz);
        string band = freqMhz >= 5925 ? "6 GHz" : freqMhz >= 4900 ? "5 GHz" : freqMhz >= 2400 ? "2.4 GHz" : "";
        string standard = StandardFromBitrate(rxBitrate);
        string signal = signalDbm != null ? $"📶 {dbm} dBm" : "-";
        string? percent = signalDbm != null ? $"{Math.Clamp(2 * (dbm + 100), 0, 100)}%" : null;

        var (ipv4, ipv6, prefix) = ReadAddresses(iface);
        string? gateway = ReadGateway(iface);
        var dns = ReadDns();
        var (sent, recv) = ReadProcNetDev(iface);

        string status = connected || ssid != null ? "Connected" : "Disconnected";
        return new WifiSnapshot(iface, status, ssid ?? "Not connected",
            string.Join(" ", new[] { "802.11", standard, band }.Where(s => !string.IsNullOrEmpty(s))),
            signal, ipv4, ipv6, sent, recv)
        {
            WifiStandard = string.IsNullOrEmpty(standard) ? null : standard,
            SignalPercentText = percent,
            Bssid = bssid,
            Gateway = gateway,
            Dns1 = dns.ElementAtOrDefault(0),
            Dns2 = dns.ElementAtOrDefault(1),
            Netmask = prefix,
            DhcpServer = ReadDhcp(iface),
            DownloadText = recv is long rx ? $"{rx / 1048576.0:F1} MB since boot" : null,
            UploadText = sent is long tx ? $"{tx / 1048576.0:F1} MB since boot" : null
        };
    }

    private static string? FindWifiIface()
    {
        string sys = "/sys/class/net";
        if (!Directory.Exists(sys)) return null;
        foreach (string dir in Directory.GetDirectories(sys))
        {
            string name = Path.GetFileName(dir);
            if (name == "lo") continue;
            if (Directory.Exists(Path.Combine(dir, "wireless")) ||
                Directory.Exists(Path.Combine(dir, "phy80211")))
                return name;
        }
        return null;
    }

    // /proc/net/dev: "iface: rx_bytes rx_packets ... tx_bytes ..."  rx is field 1, tx is field 9
    private static (long? rx, long? tx) ReadProcNetDev(string iface)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/net/dev"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0 || line[..colon].Trim() != iface) continue;
                string[] f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) return (null, null);
                long.TryParse(f[0], out long rx);
                long.TryParse(f[8], out long tx);
                return (rx, tx);
            }
        }
        catch { }
        return (null, null);
    }

    // /proc/net/route: dest 00000000, gateway is little-endian hex
    private static string? ReadGateway(string iface)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/net/route"))
            {
                string[] f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3 || f[0] != iface || f[1] != "00000000") continue;
                if (!uint.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint g)) continue;
                return $"{g & 0xFF}.{(g >> 8) & 0xFF}.{(g >> 16) & 0xFF}.{(g >> 24) & 0xFF}";
            }
        }
        catch { }
        return null;
    }

    private static List<string> ReadDns()
    {
        string path = File.Exists("/run/systemd/resolve/resolv.conf")
            ? "/run/systemd/resolve/resolv.conf"   // real upstream, not the 127.0.0.53 stub
            : "/etc/resolv.conf";
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith("nameserver ", StringComparison.Ordinal)) continue;
            string ip = line["nameserver ".Length..].Trim();
            if (ip != "127.0.0.53") list.Add(ip);
        }
        return list;
    }

    private static string? ReadDhcp(string iface)
    {
        // nmcli -t escapes ':' as '\:' — do not Split(':')
        string raw = CommandRunner.Run("nmcli", $"-t -f DHCP4.OPTION device show {iface}", 2000);
        var m = Regex.Match(raw, @"dhcp_server_identifier\s*=\s*([0-9.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string StandardFromBitrate(string? rx)
    {
        if (string.IsNullOrEmpty(rx)) return "";
        if (rx.Contains("EHT")) return "Wi-Fi 7 (802.11be)";
        if (rx.Contains("HE")) return "Wi-Fi 6 (802.11ax)";
        if (rx.Contains("VHT")) return "Wi-Fi 5 (802.11ac)";
        if (rx.Contains("HT")) return "Wi-Fi 4 (802.11n)";
        return "802.11a/b/g";
    }

    private static (string ipv4, string ipv6, string? mask) ReadAddresses(string iface)
    {
        string ipv4 = "-", ipv6 = "-";
        string? mask = null;
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == iface);
            if (nic == null) return (ipv4, ipv6, mask);
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ipv4 == "-")
                {
                    ipv4 = ua.Address.ToString();
                    mask = ua.IPv4Mask?.ToString();
                }
                else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6 == "-"
                         && !ua.Address.IsIPv6LinkLocal)
                    ipv6 = ua.Address.ToString();
            }
        }
        catch { }
        return (ipv4, ipv6, mask);
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}