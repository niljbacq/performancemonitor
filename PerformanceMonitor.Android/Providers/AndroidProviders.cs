#pragma warning disable CA1422

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Provider;
using Android.Views;
using TaskManager.Providers;

namespace TaskManager.Droid;

internal static class Fmt
{
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
        => ip == 0 ? "-" : $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";

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
        int logical = System.Environment.ProcessorCount;

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
            percent = -1;   // baseline only; the next call has a real delta
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
                percent = -1;   // baseline only; the next call has a real delta
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

public sealed class AndroidDiskProvider : IDiskProvider, IDiskListProvider, IStorageExtrasProvider
{
    private StorageExtras? _extras;
    private DateTime _extrasAtUtc;
    private bool _lastUsageAccess;
    private (bool Images, bool Videos, bool Audio) _lastMediaAccess;

    public DiskSpecs GetSpecs()
    {
        var d = BuildDiskInfo();
        return new DiskSpecs
        {
            DiskName = d.DiskName,
            ModelName = d.ModelName,
            Type = d.Type,
            Capacity = d.Capacity,
            Formatted = d.Formatted,
            SystemDisk = d.SystemDisk,
            PageFile = d.PageFile,
            UsedText = d.UsedText,
            FreeText = d.FreeText,
            AppsAndDataText = d.AppsAndDataText,
            SystemReservedText = d.SystemReservedText
        };
    }

    public IReadOnlyList<DiskInfo> GetDiskList() => new[] { BuildDiskInfo() };

    public IReadOnlyList<DiskInfo> SampleDiskList() => new[] { BuildDiskInfo() };

    // Total (StorageStatsManager, else StatFs), free (StatFs AvailableBytes) and
    // StatFs total for the apps-and-data row, so GetSpecs and the breakdown agree.
    private (double Total, double? Free, double? DataTotal, bool FromStorageStats)? ReadStorageNumbers()
    {
        double? dataTotal = null, dataFree = null;
        try
        {
            string path = Android.OS.Environment.DataDirectory?.AbsolutePath ?? "/data";
            using var stat = new Android.OS.StatFs(path);
            dataTotal = stat.TotalBytes;
            dataFree = stat.AvailableBytes;
        }
        catch { }

        // The partition total does not change while mounted, and GetTotalBytes is a
        // slow binder call, so it is read once instead of every 2 s tick.
        double? partTotal = PartTotal();
        bool fromStorageStats = partTotal != null;
        double total = partTotal ?? dataTotal ?? 0;
        return total > 0 ? (total, dataFree, dataTotal, fromStorageStats) : null;
    }

    private double? _partitionTotal;

    private double? PartTotal()
    {
        if (_partitionTotal is double cached) return cached;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var ssm = Application.Context.GetSystemService(Context.StorageStatsService)
                          as Android.App.Usage.StorageStatsManager;
                var uuid = Android.OS.Storage.StorageManager.UuidDefault;
                if (ssm != null && uuid != null)
                    _partitionTotal = ssm.GetTotalBytes(uuid);
            }
        }
        catch { }
        return _partitionTotal;
    }

    private DiskInfo BuildDiskInfo()
    {
        var d = new DiskInfo { DiskName = "Internal storage" };
        if (ReadStorageNumbers() is not { } numbers) return d;
        var (total, free, dataTotal, fromStorageStats) = numbers;

        d.Capacity = $"{Math.Round(total / (1024.0 * 1024 * 1024), 0)} GB";
        d.Formatted = d.Capacity;
        if (free is not double f) return d;

        d.FreeBytes = f;
        double used = total - f;
        d.UsedText = $"{Fmt.Gb(used)} ({used / total * 100:F1}%)";
        d.FreeText = $"{Fmt.Gb(f)} ({f / total * 100:F1}%)";

        if (dataTotal is double dt)
        {
            d.AppsAndDataText = $"{Fmt.Gb(dt - f)} ({(dt - f) / total * 100:F1}%)";
            if (fromStorageStats && total > dt)
                d.SystemReservedText = $"{Fmt.Gb(total - dt)} ({(total - dt) / total * 100:F1}%)";
        }
        return d;
    }

    public StorageExtras GetExtras()
    {
        bool usageAccess = HasUsageAccess();
        if (usageAccess != _lastUsageAccess)
        {
            _extras = null;
            _lastUsageAccess = usageAccess;
        }

        var mediaAccess = MediaAccess();
        if (mediaAccess != _lastMediaAccess)
        {
            _extras = null;
            _lastMediaAccess = mediaAccess;
        }

        if (_extras != null && DateTime.UtcNow - _extrasAtUtc < TimeSpan.FromSeconds(30))
            return _extras;
        _extras = BuildExtras();
        _extrasAtUtc = DateTime.UtcNow;
        return _extras;
    }

    private static bool HasUsageAccess()
    {
        try
        {
            var ops = Application.Context.GetSystemService(Context.AppOpsService) as AppOpsManager;
            return ops?.CheckOpNoThrow(
                "android:get_usage_stats",
                Android.OS.Process.MyUid(),
                Application.Context.PackageName!) == AppOpsManagerMode.Allowed;
        }
        catch { return false; }
    }

    public void OpenUsageAccessSettings()
    {
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionUsageAccessSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("PerfMon", $"Could not open usage access settings: {e.Message}");
        }
    }

    public void RequestMediaAccess() => MediaAccessHost.Request?.Invoke();

    private static (bool Images, bool Videos, bool Audio) MediaAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return (
                Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaImages) == Permission.Granted,
                Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaVideo) == Permission.Granted,
                Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaAudio) == Permission.Granted);

        bool external = Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted;
        return (external, external, external);
    }

    // null means the read failed; a failed read is not a measured zero.
    // A query that ran but matched nothing is a measured zero (SUM over no rows is NULL).
    private static long? SumMedia(Android.Net.Uri? uri)
    {
        if (uri == null) return null;
        try
        {
            string column = $"SUM({MediaStore.IMediaColumns.Size})";
            string? selection = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? $"{MediaStore.IMediaColumns.IsPending} = 0" : null;
            var resolver = Application.Context.ContentResolver;
            if (resolver == null) return null;
            using var cursor = resolver.Query(uri, new[] { column }, selection, null, null);
            if (cursor == null) return null;
            if (!cursor.MoveToFirst() || cursor.IsNull(0)) return 0;
            return cursor.GetLong(0);
        }
        catch { return null; }
    }

    private static Android.Net.Uri? MediaUri(string kind)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return kind switch
            {
                "images" => MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternal),
                "videos" => MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternal),
                _ => MediaStore.Audio.Media.GetContentUri(MediaStore.VolumeExternal)
            };
        }

        return kind switch
        {
            "images" => MediaStore.Images.Media.ExternalContentUri,
            "videos" => MediaStore.Video.Media.ExternalContentUri,
            _ => MediaStore.Audio.Media.ExternalContentUri
        };
    }

    private StorageExtras BuildExtras()
    {
        var extras = new StorageExtras();

        try
        {
            foreach (string line in File.ReadLines("/proc/mounts"))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4 || fields[1] != "/data") continue;
                extras.FileSystem = $"{fields[2]}{(fields[3].Split(',').Contains("ro") ? " (read-only)" : " (read-write)")}";
                break;
            }
        }
        catch { }

        try
        {
            using var stat = new Android.OS.StatFs("/data");
            long blockSize = stat.BlockSizeLong;
            extras.BlockSize = blockSize >= 1024 ? $"{blockSize / 1024} KB" : $"{blockSize} B";
        }
        catch { }

        bool usageAccess = HasUsageAccess();
        extras.UsageAccessGranted = usageAccess;
        var media = MediaAccess();
        extras.MediaAccessGranted = media.Images && media.Videos && media.Audio;
        var numbers = ReadStorageNumbers();

        if (usageAccess && numbers is { Free: double freeBytes } totals)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    var ssm = Application.Context.GetSystemService(Context.StorageStatsService)
                              as Android.App.Usage.StorageStatsManager;
                    var uuid = Android.OS.Storage.StorageManager.UuidDefault;
                    var user = Android.OS.Process.MyUserHandle();
                    if (ssm != null && uuid != null && user != null)
                    {
                        var stats = ssm.QueryStatsForUser(uuid, user);
                        if (stats != null)
                        {
                            double used = totals.Total - freeBytes;
                            long? photos = media.Images ? SumMedia(MediaUri("images")) : null;
                            long? videos = media.Videos ? SumMedia(MediaUri("videos")) : null;
                            long? audio = media.Audio ? SumMedia(MediaUri("audio")) : null;

                            long other = Math.Max(0, (long)used - stats.AppBytes - stats.DataBytes
                                - (photos ?? 0) - (videos ?? 0) - (audio ?? 0));
                            extras.Breakdown = new StorageBreakdown
                            {
                                Apps = Fmt.Bytes(stats.AppBytes),
                                AppData = Fmt.Bytes(Math.Max(0, stats.DataBytes - stats.CacheBytes)),
                                AppCache = Fmt.Bytes(stats.CacheBytes),
                                // An unreadable total stays in Other and the row hides.
                                Photos = photos is long p ? Fmt.Bytes(p) : null,
                                Videos = videos is long v ? Fmt.Bytes(v) : null,
                                Audio = audio is long a ? Fmt.Bytes(a) : null,
                                Other = Fmt.Bytes(other)
                            };
                        }
                    }
                }
            }
            catch { }
        }

        extras.Volumes = ReadVolumes();
        return extras;
    }

    private static IReadOnlyList<StorageVolumeInfo> ReadVolumes()
    {
        var volumes = new List<StorageVolumeInfo>();
        try
        {
            var sm = Application.Context.GetSystemService(Context.StorageService)
                     as Android.OS.Storage.StorageManager;
            var volumeList = sm?.StorageVolumes;
            if (volumeList == null) return volumes;

            Android.App.Usage.StorageStatsManager? ssm = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                ssm = Application.Context.GetSystemService(Context.StorageStatsService)
                      as Android.App.Usage.StorageStatsManager;

            foreach (var v in volumeList)
            {
                try
                {
                    if (v.IsPrimary) continue;
                    if (v.State != "mounted") continue;

                    double? total = null, free = null;
                    Java.Util.UUID? uuid = null;
                    try
                    {
                        if (v.Uuid != null) uuid = Java.Util.UUID.FromString(v.Uuid);
                    }
                    catch { }

                    if (OperatingSystem.IsAndroidVersionAtLeast(26) && ssm != null && uuid != null)
                    {
                        total = ssm.GetTotalBytes(uuid);
                        free = ssm.GetFreeBytes(uuid);
                    }
                    if (total == null && OperatingSystem.IsAndroidVersionAtLeast(30) && v.Directory != null)
                    {
                        using var stat = new Android.OS.StatFs(v.Directory.AbsolutePath);
                        total = stat.TotalBytes;
                        free = stat.AvailableBytes;
                    }
                    if (total is not double t || t <= 0) continue;
                    // Without a free figure the fill would read as 100% used; hide instead.
                    if (free is not double f) continue;

                    double used = Math.Max(0, t - f);
                    string? name = v.GetDescription(Application.Context);
                    volumes.Add(new StorageVolumeInfo
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Storage" : name,
                        UsageText = $"{Fmt.Bytes(used)} of {Fmt.Bytes(t)}",
                        UsedPercent = used / t * 100
                    });
                }
                catch { }
            }
        }
        catch { }
        return volumes;
    }

    // System-wide disk I/O needs root on Android; this app's own I/O is not shown,
    // so the sample is skipped and the read/write rows hide.
    public DiskSample? Sample() => null;
}

public sealed class AndroidGpuProvider : IGpuProvider, IGpuListProvider, IGpuEnvironmentProvider
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

    private List<GpuInfo>? _cached;
    private readonly object _cacheLock = new();

    public IReadOnlyList<GpuInfo> GetGpuList()
    {
        lock (_cacheLock)
        {
            return _cached ??= BuildGpuList();
        }
    }

    private static List<GpuInfo> BuildGpuList()
    {
        var gl = GpuInfoEgl.Read();

        string soc = $"{Android.OS.Build.Hardware ?? "Unknown"} (SoC / board)";
        string? model = gl?.Renderer
                        ?? Fmt.ReadText("/sys/kernel/gpu/gpu_model")
                        ?? Fmt.ReadText("/sys/class/kgsl/kgsl-3d0/gpu_model");

        string? gles = TruncateGlVersion(gl?.Version);
        string? vulkan = ReadVulkanVersion();

        return new List<GpuInfo>
        {
            new()
            {
                Name = string.IsNullOrWhiteSpace(model) ? soc : model,
                Vendor = gl?.Vendor,
                GraphicsApi = gles == null ? null
                    : vulkan == null ? gles
                    : $"{gles} · Vulkan {vulkan}",
                DriverVersion = DriverBuild(gl?.Version),
                // DriverDate/Location have no Android equivalent; null hides them.
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

    // "OpenGL ES 3.2 v1.r54p1-12eac0..." -> "v1.r54p1-12eac0..."
    private static string? DriverBuild(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        int marker = version.IndexOf(" v", StringComparison.Ordinal);
        return marker > 0 ? version[(marker + 1)..].Trim() : null;
    }

    // android.hardware.vulkan.version packs major/minor/patch as Vulkan does.
    private static string? ReadVulkanVersion()
    {
        try
        {
            var features = Application.Context.PackageManager?.GetSystemAvailableFeatures();
            if (features == null) return null;
            foreach (var f in features)
            {
                if (f.Name != "android.hardware.vulkan.version" || f.Version == 0) continue;
                int v = f.Version;
                int major = v >> 22;
                int minor = (v >> 12) & 0x3FF;
                int patch = v & 0xFFF;
                return patch > 0 ? $"{major}.{minor}.{patch}" : $"{major}.{minor}";
            }
        }
        catch { }
        return null;
    }

    public IReadOnlyList<GpuInfo> SampleGpuList()
    {
        var list = new List<GpuInfo>();
        foreach (var g in GetGpuList())
        {
            var info = Clone(g);
            info.UtilizationText = TryReadGpuUtilization();
            info.MemoryUsage = null;

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

    public GpuEnvironment GetEnvironment()
    {
        var env = new GpuEnvironment();
        try
        {
            var dm = Application.Context.GetSystemService(Context.DisplayService) as DisplayManager;
            var display = dm?.GetDisplay(0);
            if (display == null) return env;

            var metrics = new Android.Util.DisplayMetrics();
            display.GetRealMetrics(metrics);
            env.DisplayResolution = $"{metrics.WidthPixels} × {metrics.HeightPixels}";

            int current = (int)Math.Round(display.RefreshRate);
            int max = current;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                var modes = display.GetSupportedModes();
                if (modes != null && modes.Length > 0)
                    max = (int)Math.Round(modes.Max(m => m.RefreshRate));
            }
            env.RefreshRate = max > current ? $"{current} Hz (max {max} Hz)" : $"{current} Hz";

            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                var types = display.GetHdrCapabilities()?.GetSupportedHdrTypes() ?? Array.Empty<HdrType>();
                var names = types.Select(t => t switch
                {
                    HdrType.DolbyVision => "Dolby Vision",
                    HdrType.Hdr10 => "HDR10",
                    HdrType.Hlg => "HLG",
                    _ => (string?)null
                }).Where(n => n != null).ToList();
                if (OperatingSystem.IsAndroidVersionAtLeast(29) && types.Contains(HdrType.Hdr10Plus))
                    names.Add("HDR10+");
                env.Hdr = names.Count == 0 ? "No" : string.Join(", ", names);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                env.WideColor = display.IsWideColorGamut ? "Yes" : "No";

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                var power = Application.Context.GetSystemService(Context.PowerService) as PowerManager;
                if (power != null)
                {
                    env.ThermalStatus = power.CurrentThermalStatus switch
                    {
                        ThermalStatus.None => "None",
                        ThermalStatus.Light => "Light",
                        ThermalStatus.Moderate => "Moderate",
                        ThermalStatus.Severe => "Severe",
                        ThermalStatus.Critical => "Critical",
                        ThermalStatus.Emergency => "Emergency",
                        ThermalStatus.Shutdown => "Shutdown",
                        _ => null
                    };
                }
            }
        }
        catch (Exception e)
        {
            // Sampled every 2 s, so this stays at debug level instead of flooding logcat.
            Android.Util.Log.Debug("PerfMon", $"GPU environment probe failed: {e.Message}");
        }
        return env;
    }
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

        string? bssid;
        if (!hasLocation) bssid = "Location permission required";
        else if (string.IsNullOrEmpty(info.BSSID) || info.BSSID == "02:00:00:00:00:00") bssid = null;
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
        string? nonMobile = null, mobile = null, download = null, upload = null;
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
            SignalPercentText = rssiValid ? $"{Math.Clamp(2 * (rssi + 100), 0, 100)}%" : null,
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

    private static string? MaskFromPrefix(int prefix)
    {
        if (prefix < 0 || prefix > 32) return null;
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }
}
