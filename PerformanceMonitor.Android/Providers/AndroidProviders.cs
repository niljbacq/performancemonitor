#pragma warning disable CA1422

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

    // Same as Ip but returns null for 0, so callers can hide the row.
    public static string? IpOrNull(int ip)
        => ip == 0 ? null : $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";

    public static string? ReadText(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
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
    private bool _procStatPrimed;

    private long _prevIdleUs;
    private long _prevWallMs;
    private bool _cpuidlePrimed;

    private string? _usageNote;
    private readonly int[] _cores = FindCores();

    public long UptimeMilliseconds => Android.OS.SystemClock.ElapsedRealtime();
    public string? UsageUnavailableMessage => _usageNote;

    // System-wide process stats need root on Android; the app-scoped equivalents
    // are not shown, so all three rows hide.
    public string? ProcessStatsUnavailableMessage => Unavailable;

    public CpuSpecs GetSpecs()
    {
        int logical = Environment.ProcessorCount;

        var mins = new List<long>();
        var maxs = new List<long>();
        var clusters = new Dictionary<(long Min, long Max), int>();

        foreach (int core in _cores)
        {
            long? min = ReadKhz(core, "cpuinfo_min_freq");
            long? max = ReadKhz(core, "cpuinfo_max_freq");
            if (min is not long lo || max is not long hi) continue;

            mins.Add(lo);
            maxs.Add(hi);
            clusters[(lo, hi)] = clusters.TryGetValue((lo, hi), out int n) ? n + 1 : 1;
        }

        string? maxFreq = maxs.Count > 0 ? Fmt.Mhz(maxs.Max()) : null;
        string? clusterText = clusters.Count > 0
            ? string.Join(", ", clusters
                .OrderByDescending(c => c.Key.Max).ThenByDescending(c => c.Key.Min)
                .Select(c => $"{c.Value}× {c.Key.Min / 1000}–{c.Key.Max / 1000} MHz"))
            : null;

        var abis = Android.OS.Build.SupportedAbis;

        string? hardware = ReadSocName() ?? Android.OS.Build.Hardware;

        return new CpuSpecs
        {
            Name = hardware ?? "Unknown",
            MarketingName = MarketingNameFor(hardware),
            Cores = logical.ToString(),
            LogicalProcessors = logical.ToString(),
            BaseSpeed = maxFreq,
            Speed = maxFreq,
            FrequencyMinText = mins.Count > 0 ? Fmt.Mhz(mins.Min()) : null,
            FrequencyMaxText = maxFreq,
            CpuGovernor = Fmt.ReadText($"{CpuRoot}/cpufreq/policy0/scaling_governor")
                          ?? Fmt.ReadText($"{CpuRoot}/cpu0/cpufreq/scaling_governor"),
            SupportedAbis = abis != null && abis.Any() ? string.Join(", ", abis) : null,
            ClusterText = clusterText
        };
    }

    public string? PerCoreFrequencyText
    {
        get
        {
            if (_cores.Length == 0) return null;

            var parts = new List<string>();
            bool any = false;
            foreach (int core in _cores)
            {
                long? khz = ReadKhz(core, "scaling_cur_freq");
                if (khz is long k) { parts.Add((k / 1000).ToString()); any = true; }
                else parts.Add("–");
            }
            return any ? string.Join(", ", parts) + " MHz" : null;
        }
    }

    public string? FrequencyResidencyText
    {
        get
        {
            try
            {
                var totals = new SortedDictionary<long, long>();
                foreach (int core in _cores)
                {
                    string path = $"{CpuRoot}/cpu{core}/cpufreq/stats/time_in_state";
                    if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadLines(path))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        if (!long.TryParse(parts[0], out long khz)) continue;
                        if (!long.TryParse(parts[1], out long ticks)) continue;
                        totals[khz] = totals.TryGetValue(khz, out long t) ? t + ticks : ticks;
                    }
                }
                if (totals.Count == 0) return null;
                long grand = 0;
                foreach (var kv in totals) grand += kv.Value;
                if (grand <= 0) return null;
                var top = totals.OrderByDescending(kv => kv.Value).Take(3)
                    .Where(kv => kv.Value > 0)
                    .Select(kv => $"{kv.Key / 1000} MHz {kv.Value * 100.0 / grand:F0}%");
                string text = string.Join(", ", top);
                return text.Length == 0 ? null : text;
            }
            catch { return null; }
        }
    }

    // System-wide per-process CPU needs root on Android; this app's own use is not
    // shown, so the row hides.
    public string? AppUsageText => null;

    private static string? MarketingNameFor(string? soc)
    {
        if (string.IsNullOrWhiteSpace(soc)) return null;
        string key = soc.Trim().ToLowerInvariant();

        // MediaTek base model numbers (Build.Hardware / ro.board.platform).
        // mt6789 is shared by G99/G100/G200; the suffix disambiguates.
        return key switch
        {
            "mt6789" or "mt6789v" or "mt6789g" or "mt6789u" => "Helio G99",
            "mt6789h" or "mt6789i" or "mt6789j" => "Helio G100",
            "mt6789t" => "Helio G200",
            "mt6785" or "mt6785v" => "Helio G90",
            "mt6781" => "Helio G96",
            "mt6833" => "Dimensity 700",
            "mt6853" => "Dimensity 720",
            "mt6873" => "Dimensity 800",
            "mt6877" => "Dimensity 900",
            "mt6893" => "Dimensity 1200",
            "mt6983" => "Dimensity 9000",
            _ => null
        };
    }

    public float GetUsagePercent()
    {
        // cpuidle is the working path on Android 8+: /proc/stat is SELinux-blocked
        // for non-system apps, but per-CPU idle state counters stay readable and
        // advance in real time. Utilization = 1 - idle/wall.
        if (TryReadCpuidleUsage(out float cpuidlePercent))
        {
            _usageNote = null;
            return cpuidlePercent;
        }

        // Fallback for rooted devices or old Android builds where /proc/stat is readable.
        if (TryReadProcStatUsage(out float statPercent))
        {
            _usageNote = null;
            return statPercent;
        }

        _usageNote = Unavailable;
        return 0;
    }

    // Sums every cpuidle state's "time" (microseconds) across every core, then
    // compares the delta against elapsed wall time × core count. First call only
    // primes the baseline.
    private bool TryReadCpuidleUsage(out float percent)
    {
        percent = 0;
        if (_cores.Length == 0) return false;

        long totalIdleUs = 0;
        int coresRead = 0;
        foreach (int core in _cores)
        {
            long coreIdle = ReadCoreIdleMicroseconds(core);
            if (coreIdle < 0) continue;
            totalIdleUs += coreIdle;
            coresRead++;
        }
        if (coresRead == 0) return false;

        long wallMs = Android.OS.SystemClock.ElapsedRealtime();
        if (!_cpuidlePrimed)
        {
            _prevIdleUs = totalIdleUs;
            _prevWallMs = wallMs;
            _cpuidlePrimed = true;
            return true;
        }

        long idleDelta = totalIdleUs - _prevIdleUs;
        long wallDeltaMs = wallMs - _prevWallMs;
        _prevIdleUs = totalIdleUs;
        _prevWallMs = wallMs;

        if (idleDelta < 0) idleDelta = 0;
        if (wallDeltaMs <= 0) return true;

        // Capacity over the window = cores × wall time (cpuidle "time" is µs).
        double capacityUs = (double)coresRead * wallDeltaMs * 1000.0;
        double idleFraction = idleDelta / capacityUs;
        if (idleFraction < 0) idleFraction = 0;
        if (idleFraction > 1) idleFraction = 1;
        percent = (float)((1.0 - idleFraction) * 100.0);
        return true;
    }

    private bool TryReadProcStatUsage(out float percent)
    {
        percent = 0;
        try
        {
            string? first = File.ReadLines("/proc/stat").FirstOrDefault();
            if (first == null) return false;

            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") return false;

            ulong idle = ulong.Parse(parts[4]);
            ulong iowait = parts.Length > 5 ? ulong.Parse(parts[5]) : 0;
            ulong total = 0;
            for (int i = 1; i < Math.Min(parts.Length, 9); i++)
                if (ulong.TryParse(parts[i], out ulong v)) total += v;

            ulong idleTotal = idle + iowait;
            if (!_procStatPrimed)
            {
                _prevIdle = idleTotal;
                _prevTotal = total;
                _procStatPrimed = true;
                return true;
            }

            ulong idleDelta = idleTotal - _prevIdle;
            ulong totalDelta = total - _prevTotal;
            _prevIdle = idleTotal;
            _prevTotal = total;

            if (totalDelta == 0) return true;
            percent = (float)Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100, 0, 100);
            return true;
        }
        catch { return false; }
    }

    private static long ReadCoreIdleMicroseconds(int core)
    {
        try
        {
            string dir = $"{CpuRoot}/cpu{core}/cpuidle";
            if (!Directory.Exists(dir)) return -1;
            long sum = 0;
            bool any = false;
            foreach (var stateDir in Directory.GetDirectories(dir, "state*"))
            {
                string timePath = Path.Combine(stateDir, "time");
                if (!File.Exists(timePath)) continue;
                if (!long.TryParse(File.ReadAllText(timePath).Trim(), out long us)) continue;
                sum += us;
                any = true;
            }
            return any ? sum : -1;
        }
        catch { return -1; }
    }

    public ProcessStats GetProcessStats() => new(null, null, null);

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

    private static string? FormatKb(Dictionary<string, long> kb, string key)
        => kb.TryGetValue(key, out long v) ? $"{v / 1024.0 / 1024.0:F2} GB" : null;

    private static string FormatSwap(Dictionary<string, long> kb)
    {
        if (!kb.TryGetValue("SwapTotal", out long total) || !kb.TryGetValue("SwapFree", out long free))
            return "Swap unreadable (/proc/meminfo)";
        if (total == 0) return "Not in use";

        double used = total - free;
        return $"{used / 1024 / 1024:F2} GB / {total / 1024.0 / 1024:F2} GB ({used / total * 100:F0}%)";
    }
}

public sealed class AndroidDiskProvider : IDiskProvider
{
    public DiskSpecs GetSpecs()
    {
        var s = new DiskSpecs
        {
            DiskName = "Internal storage",
            ModelName = "System storage (I/O needs root)",
            Type = "Unknown"
            // SystemDisk / PageFile left null; Android has no equivalent.
        };

        double? dataTotal = null, dataFree = null;
        try
        {
            string path = Android.OS.Environment.DataDirectory?.AbsolutePath ?? "/data";
            using var stat = new Android.OS.StatFs(path);
            dataTotal = stat.TotalBytes;
            dataFree = stat.AvailableBytes;
        }
        catch { }

        double? total = null;
        bool fromStorageStats = false;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var ssm = Application.Context.GetSystemService(Context.StorageStatsService)
                          as Android.App.Usage.StorageStatsManager;
                var uuid = Android.OS.Storage.StorageManager.UuidDefault;
                if (ssm != null && uuid != null)
                {
                    total = ssm.GetTotalBytes(uuid);
                    fromStorageStats = true;
                }
            }
        }
        catch { }

        total ??= dataTotal;
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

                if (fromStorageStats && t > dt)
                    s.SystemReservedText = $"{Fmt.Gb(t - dt)} ({(t - dt) / t * 100:F1}%)";
            }
        }
        return s;
    }

    // System-wide disk I/O needs root on Android; this app's own I/O is not shown,
    // so the sample is skipped and the read/write rows hide.
    public DiskSample? Sample() => null;
}

public sealed class AndroidGpuProvider : IGpuProvider, IGpuListProvider
{
    private static GpuSpecs ToSpecs(GpuInfo g) => new()
    {
        Name = g.Name,
        DriverVersion = g.DriverVersion,
        DriverDate = g.DriverDate,
        GraphicsApi = g.GraphicsApi,
        PhysicalLocation = g.PhysicalLocation,
        Vendor = g.Vendor
    };

    private static GpuInfo Clone(GpuInfo src) => new()
    {
        DeviceId = src.DeviceId,
        PciBus = src.PciBus,
        Name = src.Name,
        Kind = src.Kind,
        DriverVersion = src.DriverVersion,
        DriverDate = src.DriverDate,
        GraphicsApi = src.GraphicsApi,
        PhysicalLocation = src.PhysicalLocation,
        Vendor = src.Vendor,
        Utilization = src.Utilization,
        TemperatureC = src.TemperatureC,
        UtilizationText = src.UtilizationText,
        TemperatureText = src.TemperatureText,
        MemoryUsage = src.MemoryUsage,
        SharedMemoryUsage = src.SharedMemoryUsage
    };

    public GpuSpecs GetSpecs()
    {
        var list = GetGpuList();
        return list.Count == 0 ? new GpuSpecs() : ToSpecs(list[0]);
    }

    public GpuSample? Sample() => null;

    public IReadOnlyList<GpuInfo> GetGpuList()
    {
        var gl = GpuInfoEgl.Read();

        string soc = $"{Android.OS.Build.Hardware ?? "Unknown"} (SoC / board)";
        string? model = gl?.Renderer
                        ?? Fmt.ReadText("/sys/kernel/gpu/gpu_model")
                        ?? Fmt.ReadText("/sys/class/kgsl/kgsl-3d0/gpu_model");

        return new List<GpuInfo>
        {
            new()
            {
                Name = string.IsNullOrWhiteSpace(model) ? soc : model,
                Vendor = gl?.Vendor,
                GraphicsApi = TruncateGlVersion(gl?.Version),
                // DriverVersion/Date/Location have no Android equivalent; null hides them.
                Kind = "Integrated"
            }
        };
    }

    // "OpenGL ES 3.2 v1.r54p1-12eac0..." -> "OpenGL ES 3.2"
    private static string? TruncateGlVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        int marker = version.IndexOf(" v", StringComparison.Ordinal);
        return marker > 0 ? version[..marker].Trim() : version.Trim();
    }

    public IReadOnlyList<GpuInfo> SampleGpuList()
    {
        var list = new List<GpuInfo>();
        foreach (var g in GetGpuList())
        {
            var info = Clone(g);
            info.UtilizationText = TryReadGpuUtilization();
            info.MemoryUsage = null;

            string? temp = ReadGpuTemperature() ?? ReadFallbackThermal();
            info.TemperatureText = temp;

            list.Add(info);
        }
        return list;
    }

    // Vendor GPU-busy nodes, in order: MediaTek, Adreno, Mali. Values are a
    // percentage ("42" or "42 %"); anything else is treated as unavailable and the
    // row hides. Cumulative busy/total counters are deliberately not used — they are
    // a since-boot average, not current utilization.
    private static string? TryReadGpuUtilization()
    {
        string[] paths =
        {
            "/sys/kernel/gpu/gpu_busy",
            "/sys/kernel/gpu/gpu_load",
            "/sys/class/kgsl/kgsl-3d0/gpu_busy_percentage",
            "/sys/class/misc/mali0/device/utilization",
            "/sys/devices/platform/mali.0/utilization"
        };

        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                string raw = File.ReadAllText(path).Replace("%", "").Trim();
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double v)
                    && v >= 0 && v <= 100)
                    return $"{v:F0}%";
            }
            catch { }
        }
        return null;
    }

    private static string? ReadGpuTemperature()
    {
        try
        {
            const string root = "/sys/class/thermal";
            if (!Directory.Exists(root)) return null;
            foreach (string zone in Directory.GetDirectories(root, "thermal_zone*"))
            {
                string? type = Fmt.ReadText(Path.Combine(zone, "type"));
                if (type == null || !IsGpuZone(type)) continue;
                long? raw = Fmt.ReadLong(Path.Combine(zone, "temp"));
                if (raw == null) continue;
                double c = raw > 1000 ? raw.Value / 1000.0 : raw.Value;
                return $"{c:F1} °C ({type})";
            }
            return null;
        }
        catch { return null; }
    }

    private static bool IsGpuZone(string type)
    {
        string t = type.ToLowerInvariant();
        return t.Contains("gpu") || t.Contains("g3d") || t.Contains("gpuss")
            || t.Contains("mali") || t.Contains("kgsl");
    }

    private static string? ReadFallbackThermal()
    {
        try
        {
            var filter = new IntentFilter(Intent.ActionBatteryChanged);
            var battery = Application.Context.RegisterReceiver(null, filter);
            int tenths = battery?.GetIntExtra(Android.OS.BatteryManager.ExtraTemperature, int.MinValue) ?? int.MinValue;
            if (tenths != int.MinValue && tenths > 0)
                return $"{tenths / 10.0:F1} °C (battery)";
        }
        catch { }

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)
                && Application.Context.GetSystemService(Context.PowerService) is Android.OS.PowerManager pm)
            {
                float headroom = pm.GetThermalHeadroom(0);
                if (!float.IsNaN(headroom))
                    return $"Thermal headroom {headroom:F2} ({(headroom >= 1f ? "fine" : "throttling")})";
            }
        }
        catch { }

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)
                && Application.Context.GetSystemService(Context.PowerService) is Android.OS.PowerManager pm)
            {
                int status = (int)pm.CurrentThermalStatus;
                if (status > 0) return $"SoC thermal: {ThermalStatusText(status)}";
            }
        }
        catch { }

        return null;
    }

    private static string ThermalStatusText(int status) => status switch
    {
        1 => "light",
        2 => "moderate",
        3 => "severe",
        4 => "critical",
        5 => "emergency",
        6 => "shutdown",
        _ => "none"
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
        else if (string.IsNullOrEmpty(info.BSSID) || info.BSSID == "02:00:00:00:00:00") bssid = Fmt.NA;
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
            DhcpServer = dhcp == null ? null : Fmt.IpOrNull(dhcp.ServerAddress),
            Gateway = dhcp == null ? null : Fmt.IpOrNull(dhcp.Gateway),
            Dns1 = dhcp == null ? null : Fmt.IpOrNull(dhcp.Dns1),
            Dns2 = dhcp == null ? null : Fmt.IpOrNull(dhcp.Dns2),
            Netmask = dhcp != null && dhcp.Netmask != 0 ? Fmt.Ip(dhcp.Netmask) : prefixMask,
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
                _ => null
            };
        }
        catch { return null; }
    }

    private readonly record struct Traffic(long TotalTx, long TotalRx, long MobileTx, long MobileRx);

    private static Traffic? ReadTraffic()
    {
        try
        {
            long totalTx = TrafficStats.TotalTxBytes;
            long totalRx = TrafficStats.TotalRxBytes;
            if (totalTx < 0 || totalRx < 0) return null;

            return new Traffic(
                totalTx, totalRx,
                Math.Max(0, TrafficStats.MobileTxBytes),
                Math.Max(0, TrafficStats.MobileRxBytes));
        }
        catch { return null; }
    }

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
            if (caps == null || !caps.HasTransport(TransportType.Wifi)) return (ipv6, netmask);

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
