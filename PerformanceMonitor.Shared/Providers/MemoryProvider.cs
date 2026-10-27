using Hardware.Info;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

public sealed class HardwareInfoMemoryProvider : IMemoryProvider
{
    private readonly HardwareInfo _hardwareInfo = new();
    private DateTime _swapAt;
    private string? _swapText;

    public MemoryUsage GetUsage()
    {
        const double gb = 1024.0 * 1024 * 1024;
        var details = new List<MemoryDetail>();
        double totalGB, usedGB, percent;

        if (TryRead(out ulong total, out ulong available, out var perf))
        {
            totalGB = Math.Round(total / gb, 1);
            usedGB = Math.Round((total - available) / gb, 1);
            percent = total > 0 ? Math.Round((total - available) * 100.0 / total, 0) : 0;
            details.AddRange(DetailRows(perf));
        }
        else
        {
            // Counters unavailable: keep the numbers, hide the extra rows.
            _hardwareInfo.RefreshMemoryStatus();
            ulong totalBytes = _hardwareInfo.MemoryStatus.TotalPhysical;
            ulong usedBytes = totalBytes - _hardwareInfo.MemoryStatus.AvailablePhysical;
            totalGB = Math.Round(totalBytes / gb, 1);
            usedGB = Math.Round(usedBytes / gb, 1);
            percent = totalBytes > 0 ? Math.Round(usedBytes * 100.0 / totalBytes, 0) : 0;
        }

        return new MemoryUsage(totalGB, usedGB, percent)
        {
            SwapText = ReadSwap(),
            Details = details
        };
    }

    // The page-file WMI query is slow and the view ticks every second, so the
    // text is shared for 30 s.
    private string? ReadSwap()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (_swapText != null && DateTime.UtcNow - _swapAt < TimeSpan.FromSeconds(30))
            return _swapText;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            ulong allocMb = 0, usedMb = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                allocMb += Convert.ToUInt64(o["AllocatedBaseSize"]);
                usedMb += Convert.ToUInt64(o["CurrentUsage"]);
            }
            _swapText = allocMb == 0
                ? "No page file"
                : $"{usedMb / 1024.0:F2} GB / {allocMb / 1024.0:F2} GB ({usedMb * 100.0 / allocMb:F0}%)";
        }
        catch { _swapText = "Page file unreadable (WMI)"; }

        _swapAt = DateTime.UtcNow;
        return _swapText;
    }

    private static bool TryRead(out ulong total, out ulong available, out PERFORMANCE_INFORMATION perf)
    {
        total = 0;
        available = 0;
        perf = default;
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return false;

            uint size = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            perf = new PERFORMANCE_INFORMATION { cb = size };
            if (!GetPerformanceInfo(out perf, size)) { perf = default; return false; }
            if (perf.PageSize == 0) return false;

            total = status.ullTotalPhys;
            available = status.ullAvailPhys;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<MemoryDetail> DetailRows(PERFORMANCE_INFORMATION p)
    {
        ulong page = p.PageSize;
        yield return new MemoryDetail("Commit",
            $"{Gb((ulong)p.CommitTotal * page)} / {Gb((ulong)p.CommitLimit * page)}");
        yield return new MemoryDetail("Cached", Gb((ulong)p.SystemCache * page));
        yield return new MemoryDetail("Paged pool", Mb((ulong)p.KernelPaged * page));
        yield return new MemoryDetail("Nonpaged pool", Mb((ulong)p.KernelNonpaged * page));
    }

    private static string Gb(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    private static string Mb(ulong bytes) => $"{bytes / (1024.0 * 1024):F0} MB";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // psapi.h PERFORMANCE_INFORMATION; counts are in pages of PageSize bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }
}

public sealed class LinuxMemoryProvider : IMemoryProvider
{
    public MemoryUsage GetUsage()
    {
        var kb = ReadMemInfo();
        double total = Kb(kb, "MemTotal");
        double available = kb.ContainsKey("MemAvailable") ? Kb(kb, "MemAvailable") : Kb(kb, "MemFree");
        double used = Math.Max(0, total - available);
        const double gb = 1024.0 * 1024.0;

        bool zram = File.Exists("/proc/swaps") &&
                    File.ReadAllText("/proc/swaps").Contains("zram", StringComparison.OrdinalIgnoreCase);

        return new MemoryUsage(
            Math.Round(total / gb, 1),
            Math.Round(used / gb, 1),
            total > 0 ? Math.Round(used / total * 100, 0) : 0)
        {
            SwapText = FormatSwap(kb, zram),
            ActiveText = FormatGb(kb, "Active"),
            InactiveText = FormatGb(kb, "Inactive"),
            SlabText = FormatGb(kb, "Slab"),
            PressureText = LinuxPressure.Memory()
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

    private static double Kb(Dictionary<string, long> kb, string key)
        => kb.TryGetValue(key, out long v) ? v : 0;

    private static string? FormatGb(Dictionary<string, long> kb, string key)
        => kb.TryGetValue(key, out long v) ? $"{v / 1024.0 / 1024.0:F2} GB" : null;

    private static string FormatSwap(Dictionary<string, long> kb, bool zram)
    {
        if (!kb.TryGetValue("SwapTotal", out long total) || !kb.TryGetValue("SwapFree", out long free))
            return "Swap unreadable (/proc/meminfo)";
        if (total == 0) return "Not in use";
        double used = total - free;
        string baseText = $"{used / 1024 / 1024:F2} GB / {total / 1024.0 / 1024:F2} GB ({used / total * 100:F0}%)";

        if (!zram) return baseText;

        // /sys/block/zram*/mm_stat columns: orig_data_size, compr_data_size, mem_used_total, ...
        string? zramDetail = null;
        try
        {
            foreach (var zramDir in Directory.GetDirectories("/sys/block", "zram*"))
            {
                string mmStatPath = Path.Combine(zramDir, "mm_stat");
                if (!File.Exists(mmStatPath)) continue;
                string line = File.ReadAllText(mmStatPath).Trim();
                string[] cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length >= 3 &&
                    long.TryParse(cols[0], out long orig) &&
                    long.TryParse(cols[1], out long compr) &&
                    long.TryParse(cols[2], out long memUsed))
                {
                    double ratio = orig > 0 ? (double)compr / orig * 100 : 0;
                    zramDetail = $", ZRAM {orig / 1024.0 / 1024:F1} MB → {compr / 1024.0 / 1024:F1} MB ({ratio:F0}%)";
                    break;
                }
            }
        }
        catch { }

        return zramDetail == null ? baseText : baseText + zramDetail;
    }
}
