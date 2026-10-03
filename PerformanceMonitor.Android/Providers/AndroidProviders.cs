#pragma warning disable CA1422 // WifiManager.ConnectionInfo is deprecated in API 31 but still works for the app's own connection

using System;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using TaskManager.Providers;

namespace TaskManager.Droid;

public sealed class AndroidCpuProvider : ICpuProvider
{
    private const string Unavailable = "Not available on Android";

    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _primed;
    private string? _usageNote;

    public long UptimeMilliseconds => Android.OS.SystemClock.ElapsedRealtime();
    public string? UsageUnavailableMessage => _usageNote;
    public string? ProcessStatsUnavailableMessage => Unavailable;

    public CpuSpecs GetSpecs()
    {
        int n = Environment.ProcessorCount;
        string? maxFreq = ReadMaxFrequency();

        return new CpuSpecs
        {
            Name = ReadSocName() ?? Android.OS.Build.Hardware ?? "Unknown",
            Cores = n.ToString(),
            LogicalProcessors = n.ToString(),
            BaseSpeed = maxFreq ?? Unavailable,
            Speed = maxFreq ?? Unavailable
        };
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
            return 0;                   // TODO(logging, item 6)
        }
    }

    public ProcessStats GetProcessStats() => new(0, 0, "N/A"); // never shown: ProcessStatsUnavailableMessage is set

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
        catch { } // TODO(logging, item 6)
        return null;
    }

    private static string? ReadMaxFrequency()
    {
        long maxKhz = 0;
        try
        {
            foreach (string dir in Directory.GetDirectories("/sys/devices/system/cpu", "cpu*"))
            {
                string name = Path.GetFileName(dir);
                if (name.Length <= 3 || !name.Skip(3).All(char.IsDigit)) continue;

                string file = Path.Combine(dir, "cpufreq", "cpuinfo_max_freq");
                if (File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), out long khz))
                    maxKhz = Math.Max(maxKhz, khz);
            }
        }
        catch { } // TODO(logging, item 6)
        return maxKhz > 0 ? $"{maxKhz / 1_000_000.0:F2} GHz" : null;
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

        return new MemoryUsage(
            Math.Round(total / gb, 1),
            Math.Round(used / gb, 1),
            total > 0 ? Math.Round(used / total * 100, 0) : 0);
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

        try
        {
            string path = Android.OS.Environment.DataDirectory?.AbsolutePath ?? "/data";
            using var stat = new Android.OS.StatFs(path);
            double totalGB = Math.Round(stat.TotalBytes / (1024.0 * 1024.0 * 1024.0), 0);
            s.Capacity = $"{totalGB} GB";
            s.Formatted = $"{totalGB} GB";
        }
        catch { } // TODO(logging, item 6)

        return s;
    }

    public DiskSample? Sample() => new DiskSample { NotSupported = true, NotSupportedMessage = Unavailable };
}

public sealed class AndroidGpuProvider : IGpuProvider
{
    private const string Unavailable = "Not available on Android";

    public GpuSpecs GetSpecs() => new()
    {
        Name = $"{Android.OS.Build.Hardware ?? "Unknown"} (SoC / board)",
        DriverVersion = Unavailable,
        DriverDate = Unavailable,
        GraphicsApi = Unavailable,
        PhysicalLocation = Unavailable
    };

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

        string rawSsid = info.SSID ?? "";
        string ssid;
        if (!HasLocationPermission()) ssid = "Location permission required";
        else if (rawSsid.Length == 0 || rawSsid == "<unknown ssid>") ssid = "Unavailable (turn on Location)";
        else ssid = rawSsid.Trim('"');

        int rssi = info.Rssi;
        string signal = rssi <= -127 ? "-" : $"📶 {rssi} dBm";

        int freq = info.Frequency;
        string band = freq >= 5925 ? "6 GHz" : freq >= 4900 ? "5 GHz" : freq >= 2400 ? "2.4 GHz" : "";
        string rate = info.LinkSpeed > 0 ? $"{info.LinkSpeed} Mbps link" : "";
        string detail = string.Join(", ", new[] { band, rate }.Where(x => x.Length > 0));
        string connectionType = detail.Length > 0 ? $"Wi-Fi ({detail})" : "Wi-Fi";

        string ipv4 = ip == 0 ? "-" : $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";
        var (sent, received) = GetWifiBytes();

        return new WifiSnapshot("Wi-Fi", "Connected", ssid, connectionType, signal, ipv4, GetIpv6(), sent, received);
    }

    private static WifiSnapshot Disconnected(string adapter)
        => new(adapter, "Disconnected", "Not connected", "-", "-", "-", "-", null, null);

    private bool HasLocationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true; // pre-6.0: granted at install
        return _context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
               == Android.Content.PM.Permission.Granted;
    }

    // Device-wide bytes minus mobile-data bytes = everything that went over non-cellular networks.
    // Approximate (includes any other non-mobile interface), so treat it as Wi-Fi throughput only roughly.
    private static (long? sent, long? received) GetWifiBytes()
    {
        try
        {
            long totalTx = TrafficStats.TotalTxBytes;
            long totalRx = TrafficStats.TotalRxBytes;
            if (totalTx < 0 || totalRx < 0) return (null, null);

            long mobTx = Math.Max(0, TrafficStats.MobileTxBytes);
            long mobRx = Math.Max(0, TrafficStats.MobileRxBytes);
            return (Math.Max(0, totalTx - mobTx), Math.Max(0, totalRx - mobRx));
        }
        catch { return (null, null); } // TODO(logging, item 6)
    }

    private string GetIpv6()
    {
        try
        {
            if (_connectivity == null || !OperatingSystem.IsAndroidVersionAtLeast(23)) return "-";

            var network = _connectivity.ActiveNetwork;
            if (network == null) return "-";

            var caps = _connectivity.GetNetworkCapabilities(network);
            if (caps == null || !caps.HasTransport(TransportType.Wifi)) return "-";   // active network isn't Wi-Fi

            var addresses = _connectivity.GetLinkProperties(network)?.LinkAddresses;
            if (addresses == null) return "-";

            foreach (var la in addresses)
            {
                if (la.Address is Java.Net.Inet6Address v6 && !v6.IsLinkLocalAddress && !v6.IsLoopbackAddress)
                    return v6.HostAddress?.Split('%')[0] ?? "-";
            }
        }
        catch { } // TODO(logging, item 6)
        return "-";
    }
}