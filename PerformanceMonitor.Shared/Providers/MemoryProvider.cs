using Hardware.Info;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

public sealed class HardwareInfoMemoryProvider : IMemoryProvider
{
    private readonly HardwareInfo _hardwareInfo = new();

    public MemoryUsage GetUsage()
    {
        _hardwareInfo.RefreshMemoryStatus();

        ulong totalBytes = _hardwareInfo.MemoryStatus.TotalPhysical;
        ulong usedBytes = totalBytes - _hardwareInfo.MemoryStatus.AvailablePhysical;

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double percent = Math.Round((usedGB / totalGB) * 100, 0);

        // Windows page file via WMI
        string? swap = null;
        if (OperatingSystem.IsWindows())
        {
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
                swap = allocMb == 0
                    ? "No page file"
                    : $"{usedMb / 1024.0:F2} GB / {allocMb / 1024.0:F2} GB ({usedMb * 100.0 / allocMb:F0}%)";
            }
            catch { swap = "Page file unreadable (WMI)"; }
        }

        return new MemoryUsage(totalGB, usedGB, percent) { SwapText = swap };
    }
}

/// <summary>
/// Linux memory via /proc/meminfo, including Swap / ZRAM / Active / Inactive / Slab
/// rows so the desktop Linux Memory tab matches the Android one.
/// </summary>
public sealed class LinuxMemoryProvider : IMemoryProvider
{
    public MemoryUsage GetUsage()
    {
        var kb = ReadMemInfo();
        double total = Kb(kb, "MemTotal");
        double available = kb.ContainsKey("MemAvailable") ? Kb(kb, "MemAvailable") : Kb(kb, "MemFree");
        double used = Math.Max(0, total - available);
        const double gb = 1024.0 * 1024.0; // meminfo values are kB

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

        if (!zram) return baseText + " swap";

        // ZRAM: read /sys/block/zram*/mm_stat for orig_data_size, compr_data_size, mem_used_total
        // mm_stat columns: orig_data_size, compr_data_size, mem_used_total, mem_limit, max_used, ...
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
                    zramDetail = $", ZRAM: {orig / 1024.0 / 1024:F1} MB → {compr / 1024.0 / 1024:F1} MB ({ratio:F0}%), {memUsed / 1024.0 / 1024:F1} MB used";
                    break;
                }
            }
        }
        catch { }

        return baseText + (zramDetail ?? " zram") + " zram";
    }
}
