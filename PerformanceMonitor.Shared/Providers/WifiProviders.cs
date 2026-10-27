using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace TaskManager.Providers;

[SupportedOSPlatform("windows")]
public sealed class WindowsWifiProvider : IWifiProvider
{
    private DateTime _netshAt;
    private string? _netshAdapter;
    private (string? Ssid, string? Bssid, string? SignalPercent, string? Radio, string? Band) _netsh;

    public WifiDetails GetDetails(string interfaceName)
    {
        // Windows uses GetSnapshot; projecting keeps one parsing path.
        var snap = GetSnapshot();
        return snap == null
            ? new WifiDetails("Disconnected", "Not connected", "-", "-")
            : new WifiDetails(snap.Status, snap.Ssid, snap.ConnectionType, snap.SignalStrength);
    }

    // netsh spawn costs ~80-120 ms here; the view model samples every 2 s,
    // so cache the parsed fields for 5 s.
    private (string? Ssid, string? Bssid, string? SignalPercent, string? Radio, string? Band)
        ReadNetsh(string adapterName, string ownMac)
    {
        if (_netshAdapter == adapterName && DateTime.UtcNow - _netshAt < TimeSpan.FromSeconds(5))
            return _netsh;

        string output = CommandRunner.Run("netsh", "wlan show interfaces", 2000);
        _netsh = ParseInterface(output, adapterName, ownMac);
        _netshAdapter = adapterName;
        _netshAt = DateTime.UtcNow;
        return _netsh;
    }

    // Labels are localized, but the values are not, so every field is found
    // by the shape of its value.
    private static (string? Ssid, string? Bssid, string? SignalPercent, string? Radio, string? Band)
        ParseInterface(string output, string adapterName, string ownMac)
    {
        // netsh prints one block per adapter, blank-line separated. Pick the
        // block for this NIC by its Name value; fall back to the first block.
        var blocks = new List<List<(string Label, string Value)>>();
        var current = new List<(string Label, string Value)>();
        foreach (string line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) blocks.Add(current);
                current = new List<(string Label, string Value)>();
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            current.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        if (current.Count > 0) blocks.Add(current);

        var pairs = blocks.FirstOrDefault(b =>
                        b.Any(p => string.Equals(p.Value, adapterName, StringComparison.OrdinalIgnoreCase)))
                    ?? blocks.FirstOrDefault()
                    ?? new List<(string Label, string Value)>();

        // The adapter's own MAC is also MAC-shaped and comes first; skip it so
        // the next one is the AP's BSSID and the line above it is the SSID.
        string own = NormalizeMac(ownMac);
        int bssidIndex = pairs.FindIndex(p => IsMac(p.Value) && NormalizeMac(p.Value) != own);
        string? bssid = bssidIndex >= 0 ? pairs[bssidIndex].Value : null;
        string? ssid = bssidIndex > 0 ? pairs[bssidIndex - 1].Value : null;
        string? signal = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^\d{1,3}\s*%$")).Value;
        string? radio = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^802\.11")).Value;
        string? band = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^(2\.4|5|6)\s*GHz$")).Value;

        return (ssid, bssid, signal, radio, band);
    }

    private static bool IsMac(string value) =>
        Regex.IsMatch(value, @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$");

    private static string NormalizeMac(string value) =>
        value.Replace(":", "").Replace("-", "").ToUpperInvariant();

    public WifiSnapshot? GetSnapshot()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .FirstOrDefault();

        string adapter = string.IsNullOrWhiteSpace(nic?.Description) ? "Wi-Fi" : nic!.Description;
        if (nic == null)
            return new WifiSnapshot(adapter, "Disconnected", "No wireless adapter", "-", "-", "-", "-", null, null);

        var parsed = ReadNetsh(nic.Name, nic.GetPhysicalAddress().ToString());

        string? bssid = parsed.Bssid;
        string? ssid = parsed.Ssid;
        string? signalText = parsed.SignalPercent;
        string? radio = parsed.Radio;
        string? band = parsed.Band;

        if (nic.OperationalStatus != OperationalStatus.Up || bssid == null)
            return new WifiSnapshot(adapter, "Disconnected", "Not connected", "-", "-", "-", "-", null, null);

        string standard = radio switch
        {
            "802.11be" => "Wi-Fi 7 (802.11be)",
            "802.11ax" => "Wi-Fi 6 (802.11ax)",
            "802.11ac" => "Wi-Fi 5 (802.11ac)",
            "802.11n" => "Wi-Fi 4 (802.11n)",
            _ => "802.11a/b/g"
        };
        int? percent = int.TryParse(signalText?.TrimEnd('%', ' '), out int p) ? Math.Clamp(p, 0, 100) : null;

        string ipv4 = "-", ipv6 = "-", mask = "-";
        string? gateway = null, dhcp = null;
        var dns = new List<string>();
        long? sent = null, received = null;
        try
        {
            var ip = nic.GetIPProperties();
            foreach (var ua in ip.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ipv4 == "-")
                {
                    ipv4 = ua.Address.ToString();
                    mask = ua.IPv4Mask?.ToString() ?? "-";
                }
                else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6 == "-"
                         && !ua.Address.IsIPv6LinkLocal)
                {
                    ipv6 = ua.Address.ToString();
                }
            }
            gateway = ip.GatewayAddresses.FirstOrDefault(g => g.Address.ToString() != "0.0.0.0")?.Address.ToString();
            dhcp = ip.DhcpServerAddresses.FirstOrDefault()?.ToString();
            dns = ip.DnsAddresses.Select(a => a.ToString()).Take(2).ToList();

            var stats = nic.GetIPv4Statistics();
            sent = stats.BytesSent;
            received = stats.BytesReceived;
        }
        catch { }

        return new WifiSnapshot(adapter, "Connected", ssid ?? "Connected",
            string.Join(" ", new[] { "802.11", standard, band }.Where(s => !string.IsNullOrEmpty(s))),
            percent is int sp ? $"📶 {sp}%" : "-", ipv4, ipv6, sent, received)
        {
            WifiStandard = standard,
            SignalPercentText = percent is int q ? $"{q}%" : null,
            Bssid = bssid,
            Gateway = gateway,
            Dns1 = dns.ElementAtOrDefault(0),
            Dns2 = dns.ElementAtOrDefault(1),
            Netmask = mask,
            DhcpServer = dhcp,
            DownloadText = received is long rx ? $"{rx / 1048576.0:F1} MB since boot" : null,
            UploadText = sent is long tx ? $"{tx / 1048576.0:F1} MB since boot" : null
        };
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