#pragma warning disable CA1422 // deprecated WifiManager APIs (ConnectionInfo, DhcpInfo) still work for the app's own connection

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using TaskManager.Providers;

namespace TaskManager.Droid;

internal static class Fmt
{
    public const string NA = "N/A";
    private const double GiB = 1024.0 * 1024.0 * 1024.0;

    public static string Gb(double bytes) => $"{bytes / GiB:F2} GB";

    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:F0} B" : $"{bytes:F2} {units[i]}";
    }

    public static string Mhz(long khz)
    {
        long mhz = khz / 1000;
        return mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz} MHz";
    }

    // Android packs an IPv4 address into an int with the FIRST octet in the lowest byte.
    public static string Ip(int ip)
        => ip == 0 ? NA : $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";

    public static string? ReadText(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; } // missing or blocked by SELinux
    }

    public static long? ReadLong(string path)
        => long.TryParse(ReadText(path), out long v) ? v : null;
}

public sealed class AndroidCpuProvider : ICpuProvider
{
    private const string Unavailable = "Not available on Android";
    private const string CpuRoot = "/sys/devices/system/cpu";

    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _primed;
    private string? _usageNote;
    private readonly int[] _cores = FindCores();

    public long UptimeMilliseconds => Android.OS.SystemClock.ElapsedRealtime();
    public string? UsageUnavailableMessage => _usageNote;
    public string? ProcessStatsUnavailableMessage => Unavailable;

    public CpuSpecs GetSpecs()
    {
        int logical = Environment.ProcessorCount;

        var mins = new List<long>();
        var maxs = new List<long>();
        var clusters = new Dictionary<(long Min, long Max), int>();   // (min,max) range -> core count

        foreach (int core in _cores)
        {
            long? min = ReadKhz(core, "cpuinfo_min_freq");
            long? max = ReadKhz(core, "cpuinfo_max_freq");
            if (min is not long lo || max is not long hi) continue;   // offline or unreadable core

            mins.Add(lo);
            maxs.Add(hi);
            clusters[(lo, hi)] = clusters.TryGetValue((lo, hi), out int n) ? n + 1 : 1;
        }

        string? maxFreq = maxs.Count > 0 ? Fmt.Mhz(maxs.Max()) : null;
        string clusterText = clusters.Count > 0
            ? string.Join(", ", clusters
                .OrderByDescending(c => c.Key.Max).ThenByDescending(c => c.Key.Min)
                .Select(c => $"{c.Value}× {c.Key.Min / 1000}–{c.Key.Max / 1000} MHz"))
            : Fmt.NA;

        var abis = Android.OS.Build.SupportedAbis;

        return new CpuSpecs
        {
            Name = ReadSocName() ?? Android.OS.Build.Hardware ?? "Unknown",
            Cores = logical.ToString(),
            LogicalProcessors = logical.ToString(),
            BaseSpeed = maxFreq ?? Unavailable,
            Speed = maxFreq ?? Unavailable,
            FrequencyMinText = mins.Count > 0 ? Fmt.Mhz(mins.Min()) : Fmt.NA,
            FrequencyMaxText = maxFreq ?? Fmt.NA,
            CpuGovernor = Fmt.ReadText($"{CpuRoot}/cpufreq/policy0/scaling_governor")
                          ?? Fmt.ReadText($"{CpuRoot}/cpu0/cpufreq/scaling_governor")
                          ?? Fmt.NA,
            SupportedAbis = abis != null && abis.Any() ? string.Join(", ", abis) : Fmt.NA,
            ClusterText = clusterText
        };
    }

    // Live value, polled every second by the ViewModel.
    public string? PerCoreFrequencyText
    {
        get
        {
            if (_cores.Length == 0) return Fmt.NA;

            var parts = new List<string>();
            bool any = false;
            foreach (int core in _cores)
            {
                long? khz = ReadKhz(core, "scaling_cur_freq");
                if (khz is long k) { parts.Add((k / 1000).ToString()); any = true; }
                else parts.Add("–");
            }
            return any ? string.Join(", ", parts) + " MHz" : Fmt.NA;
        }
    }

    public float GetUsagePercent()
    {
        try
        {
            string? first = File.ReadLines("/proc/stat").FirstOrDefault();
            if (first == null) throw new IOException("empty /proc/stat");

            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") throw new IOException("unexpected /proc/stat format");

            ulong idle = ulong.Parse(parts[4]);
            ulong iowait = parts.Length > 5 ? ulong.Parse(parts[5]) : 0;
            ulong total = 0;
            for (int i = 1; i < Math.Min(parts.Length, 9); i++)   // user..steal; guest fields are already inside user/nice
                if (ulong.TryParse(parts[i], out ulong v)) total += v;

            ulong idleTotal = idle + iowait;
            _usageNote = null;

            if (!_primed)
            {
                _prevIdle = idleTotal;
                _prevTotal = total;
                _primed = true;
                return 0;
            }

            ulong idleDelta = idleTotal - _prevIdle;
            ulong totalDelta = total - _prevTotal;
            _prevIdle = idleTotal;
            _prevTotal = total;

            if (totalDelta == 0) return 0;
            return (float)Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100, 0, 100);
        }
        catch
        {
            _usageNote = Unavailable;   // blocked or unreadable on this Android version
            return 0;
        }
    }

    public ProcessStats GetProcessStats() => new(0, 0, "N/A"); // never shown: ProcessStatsUnavailableMessage is set

    private static int[] FindCores()
    {
        try
        {
            return Directory.GetDirectories(CpuRoot, "cpu*")
                .Select(dir => Path.GetFileName(dir))
                .Where(name => name.Length > 3 && name.Skip(3).All(char.IsDigit))
                .Select(name => int.Parse(name.Substring(3)))
                .OrderBy(i => i)
                .ToArray();
        }
        catch { return Array.Empty<int>(); }
    }

    private static long? ReadKhz(int core, string file)
        => Fmt.ReadLong($"{CpuRoot}/cpu{core}/cpufreq/{file}");

    private static string? ReadSocName()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase)) continue;
                int i = line.IndexOf(':');
                if (i < 0) continue;
                string name = line[(i + 1)..].Trim();
                if (name.Length > 0) return name;
            }
        }
        catch { }
        return null;
    }
}

public sealed class AndroidMemoryProvider : IMemoryProvider
{
    private readonly ActivityManager? _activityManager =
        Application.Context.GetSystemService(Context.ActivityService) as ActivityManager;
    private readonly ActivityManager.MemoryInfo _info = new();

    public MemoryUsage GetUsage()
    {
        _activityManager?.GetMemoryInfo(_info);

        const double gb = 1024.0 * 1024.0 * 1024.0;
        double total = _info.TotalMem;
        double used = Math.Max(0, _info.TotalMem - _info.AvailMem);
        var kb = ReadMemInfo();

        return new MemoryUsage(
            Math.Round(total / gb, 1),
            Math.Round(used / gb, 1),
            total > 0 ? Math.Round(used / total * 100, 0) : 0)
        {
            SwapText = FormatSwap(kb),
            ActiveText = FormatKb(kb, "Active"),
            InactiveText = FormatKb(kb, "Inactive"),
            SlabText = FormatKb(kb, "Slab")
        };
    }

    // /proc/meminfo lines look like "SwapTotal:       7864316 kB"
    private static Dictionary<string, long> ReadMemInfo()
    {
        var result = new Dictionary<string, long>();
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string[] parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && long.TryParse(parts[0], out long value))
                    result[line[..colon]] = value;
            }
        }
        catch { }
        return result;
    }

    private static string FormatKb(Dictionary<string, long> kb, string key)
        => kb.TryGetValue(key, out long v) ? $"{v / 1024.0 / 1024.0:F2} GB" : Fmt.NA;

    private static string FormatSwap(Dictionary<string, long> kb)
    {
        if (!kb.TryGetValue("SwapTotal", out long total) || !kb.TryGetValue("SwapFree", out long free))
            return Fmt.NA;
        if (total == 0) return "Not in use";

        double used = total - free;
        return $"{used / 1024 / 1024:F2} GB / {total / 1024.0 / 1024:F2} GB ({used / total * 100:F0}%)";
    }
}

public sealed class AndroidDiskProvider : IDiskProvider
{
    private const string Unavailable = "Not available on Android";

    public DiskSpecs GetSpecs()
    {
        var s = new DiskSpecs
        {
            DiskName = "Internal storage",
            ModelName = "Internal storage",
            Type = Unavailable,
            SystemDisk = Unavailable,
            PageFile = Unavailable
        };

        // 1) The user-data partition (/data): the part apps can use. This is the old 224 GB figure.
        double? dataTotal = null, dataFree = null;
        try
        {
            string path = Android.OS.Environment.DataDirectory?.AbsolutePath ?? "/data";
            using var stat = new Android.OS.StatFs(path);
            dataTotal = stat.TotalBytes;
            dataFree = stat.AvailableBytes;
        }
        catch { }

        // 2) The whole internal storage device, including system partitions (the 238 GB figure).
        double? total = null;
        bool fromStorageStats = false;
        try
        {
            var ssm = Application.Context.GetSystemService(Context.StorageStatsService)
                      as Android.App.Usage.StorageStatsManager;
            if (ssm != null)
            {
                total = ssm.GetTotalBytes(Android.OS.Storage.StorageManager.UuidDefault);
                fromStorageStats = true;
            }
        }
        catch { }

        total ??= dataTotal;               // fallback: at least show the /data size
        if (total is not double t) return s;

        s.Capacity = $"{Math.Round(t / (1024.0 * 1024 * 1024), 0)} GB";
        s.Formatted = s.Capacity;

        if (dataFree is double free)
        {
            double used = t - free;
            s.UsedText = $"{Fmt.Gb(used)} ({used / t * 100:F1}%)";
            s.FreeText = $"{Fmt.Gb(free)} ({free / t * 100:F1}%)";

            if (dataTotal is double dt)
            {
                s.AppsAndDataText = $"{Fmt.Gb(dt - free)} ({(dt - free) / t * 100:F1}%)";

                // Only meaningful when "total" really came from the whole-device figure.
                if (fromStorageStats && t > dt)
                    s.SystemReservedText = $"{Fmt.Gb(t - dt)} ({(t - dt) / t * 100:F1}%)";
            }
        }
        return s;
    }

    public DiskSample? Sample() => new DiskSample { NotSupported = true, NotSupportedMessage = Unavailable };
}

public sealed class AndroidGpuProvider : IGpuProvider
{
    private const string Unavailable = "Not available on Android";

    public GpuSpecs GetSpecs()
    {
        // Runs on a background thread (GpuViewModel calls GetSpecs inside Task.Run), which is what EGL needs.
        var gl = GpuInfoEgl.Read();

        string soc = $"{Android.OS.Build.Hardware ?? "Unknown"} (SoC / board)";
        string? model = gl?.Renderer
                        ?? Fmt.ReadText("/sys/kernel/gpu/gpu_model")            // MediaTek, best effort
                        ?? Fmt.ReadText("/sys/class/kgsl/kgsl-3d0/gpu_model");  // Qualcomm, best effort

        return new GpuSpecs
        {
            Name = string.IsNullOrWhiteSpace(model) ? soc : model,
            Vendor = gl?.Vendor ?? Fmt.NA,
            GraphicsApi = gl?.Version ?? Unavailable,
            DriverVersion = Unavailable,
            DriverDate = Unavailable,
            PhysicalLocation = Unavailable
        };
    }

    public GpuSample? Sample() => new()
    {
        UtilizationText = Unavailable,
        TemperatureText = Unavailable,
        MemoryUsage = Unavailable
    };
}

public sealed class AndroidWifiProvider : IWifiProvider
{
    private readonly Context _context = Application.Context;
    private readonly WifiManager? _wifi;
    private readonly ConnectivityManager? _connectivity;

    public AndroidWifiProvider()
    {
        _wifi = _context.GetSystemService(Context.WifiService) as WifiManager;
        _connectivity = _context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
    }

    public WifiDetails GetDetails(string interfaceName)
    {
        var s = GetSnapshot();
        return s == null
            ? new WifiDetails("Disconnected", "Not connected", "-", "-")
            : new WifiDetails(s.Status, s.Ssid, s.ConnectionType, s.SignalStrength);
    }

    public WifiSnapshot? GetSnapshot()
    {
        if (_wifi == null) return Disconnected("Wi-Fi");
        if (!_wifi.IsWifiEnabled) return Disconnected("Wi-Fi (off)");

        var info = _wifi.ConnectionInfo;
        int ip = info?.IpAddress ?? 0;
        if (info == null || (ip == 0 && info.NetworkId == -1)) return Disconnected("Wi-Fi");

        bool hasLocation = HasLocationPermission();

        string rawSsid = info.SSID ?? "";
        string ssid;
        if (!hasLocation) ssid = "Location permission required";
        else if (rawSsid.Length == 0 || rawSsid == "<unknown ssid>") ssid = "Unavailable (turn on Location)";
        else ssid = rawSsid.Trim('"');

        string bssid;
        if (!hasLocation) bssid = "Location permission required";
        else if (string.IsNullOrEmpty(info.BSSID) || info.BSSID == "02:00:00:00:00:00") bssid = Fmt.NA;   // Android's "hidden" placeholder
        else bssid = info.BSSID;

        int rssi = info.Rssi;
        bool rssiValid = rssi > -127;
        string signal = rssiValid ? $"📶 {rssi} dBm" : "-";

        int freq = info.Frequency;
        string band = freq >= 5925 ? "6 GHz" : freq >= 4900 ? "5 GHz" : freq >= 2400 ? "2.4 GHz" : "";
        string rate = info.LinkSpeed > 0 ? $"{info.LinkSpeed} Mbps link" : "";
        string detail = string.Join(", ", new[] { band, rate }.Where(x => x.Length > 0));
        string connectionType = detail.Length > 0 ? $"Wi-Fi ({detail})" : "Wi-Fi";

        string ipv4 = ip == 0 ? "-" : Fmt.Ip(ip);
        var (ipv6, prefixMask) = ReadLinkAddresses();
        var dhcp = _wifi.DhcpInfo;

        long? sent = null, received = null;
        string nonMobile = Fmt.NA, mobile = Fmt.NA, download = Fmt.NA, upload = Fmt.NA;
        if (ReadTraffic() is Traffic tr)
        {
            long nonTx = Math.Max(0, tr.TotalTx - tr.MobileTx);
            long nonRx = Math.Max(0, tr.TotalRx - tr.MobileRx);
            double non = nonTx + nonRx;
            double mob = tr.MobileTx + tr.MobileRx;
            double all = non + mob;

            sent = nonTx;
            received = nonRx;
            nonMobile = $"{Fmt.Bytes(non)} ({(all > 0 ? non / all * 100 : 0):F0}%)";
            mobile = $"{Fmt.Bytes(mob)} ({(all > 0 ? mob / all * 100 : 0):F0}%)";
            download = Fmt.Bytes(tr.TotalRx);
            upload = Fmt.Bytes(tr.TotalTx);
        }

        return new WifiSnapshot("Wi-Fi", "Connected", ssid, connectionType, signal, ipv4, ipv6, sent, received)
        {
            WifiStandard = StandardText(info),
            SignalPercentText = rssiValid ? $"{Math.Clamp(2 * (rssi + 100), 0, 100)}%" : Fmt.NA,
            Bssid = bssid,
            DhcpServer = dhcp == null ? Fmt.NA : Fmt.Ip(dhcp.ServerAddress),
            Gateway = dhcp == null ? Fmt.NA : Fmt.Ip(dhcp.Gateway),
            Dns1 = dhcp == null ? Fmt.NA : Fmt.Ip(dhcp.Dns1),
            Dns2 = dhcp == null ? Fmt.NA : Fmt.Ip(dhcp.Dns2),
            Netmask = dhcp != null && dhcp.Netmask != 0 ? Fmt.Ip(dhcp.Netmask) : prefixMask ?? Fmt.NA,
            NonMobileTrafficText = nonMobile,
            MobileTrafficText = mobile,
            DownloadText = download,
            UploadText = upload
        };
    }

    private static WifiSnapshot Disconnected(string adapter)
        => new(adapter, "Disconnected", "Not connected", "-", "-", "-", "-", null, null);

    private bool HasLocationPermission()
        => _context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
           == Android.Content.PM.Permission.Granted;

    // WifiInfo.WifiStandard needs API 30+. The numbers are ScanResult.WIFI_STANDARD_* constants.
    private static string? StandardText(WifiInfo info)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return null;
        try
        {
            return (int)info.WifiStandard switch
            {
                1 => "802.11a/b/g (legacy)",
                4 => "Wi-Fi 4 (802.11n)",
                5 => "Wi-Fi 5 (802.11ac)",
                6 => "Wi-Fi 6 (802.11ax)",
                7 => "802.11ad",
                8 => "Wi-Fi 7 (802.11be)",
                _ => Fmt.NA
            };
        }
        catch { return Fmt.NA; }
    }

    private readonly record struct Traffic(long TotalTx, long TotalRx, long MobileTx, long MobileRx);

    // Device-wide counters since boot. "Non-mobile" = total minus mobile data (mostly Wi-Fi).
    private static Traffic? ReadTraffic()
    {
        try
        {
            long totalTx = TrafficStats.TotalTxBytes;
            long totalRx = TrafficStats.TotalRxBytes;
            if (totalTx < 0 || totalRx < 0) return null;   // -1 means unsupported

            return new Traffic(
                totalTx, totalRx,
                Math.Max(0, TrafficStats.MobileTxBytes),
                Math.Max(0, TrafficStats.MobileRxBytes));
        }
        catch { return null; }
    }

    // One pass over the Wi-Fi network's addresses: a global IPv6 address, and the IPv4 netmask from its prefix length.
    private (string ipv6, string? netmask) ReadLinkAddresses()
    {
        string ipv6 = "-";
        string? netmask = null;
        try
        {
            if (_connectivity == null) return (ipv6, netmask);

            var network = _connectivity.ActiveNetwork;
            if (network == null) return (ipv6, netmask);

            var caps = _connectivity.GetNetworkCapabilities(network);
            if (caps == null || !caps.HasTransport(TransportType.Wifi)) return (ipv6, netmask);   // active network isn't Wi-Fi

            var addresses = _connectivity.GetLinkProperties(network)?.LinkAddresses;
            if (addresses == null) return (ipv6, netmask);

            foreach (var la in addresses)
            {
                if (la.Address is Java.Net.Inet6Address v6 && ipv6 == "-" && !v6.IsLinkLocalAddress && !v6.IsLoopbackAddress)
                    ipv6 = v6.HostAddress?.Split('%')[0] ?? "-";
                else if (la.Address is Java.Net.Inet4Address && netmask == null)
                    netmask = MaskFromPrefix(la.PrefixLength);
            }
        }
        catch { }
        return (ipv6, netmask);
    }

    private static string MaskFromPrefix(int prefix)
    {
        if (prefix < 0 || prefix > 32) return Fmt.NA;
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }
}