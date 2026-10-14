using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TaskManager.Providers;

/// <summary>
/// Linux memory via /proc/meminfo, including Swap / ZRAM / Active / Inactive / Slab
/// rows so the desktop Linux Memory tab matches the Android one.
/// </summary>
public sealed class LinuxMemoryProvider : IMemoryProvider
{
    public MemoryUsage GetUsage()
    {
        Dictionary<string, long> kb = ReadMemInfoKb();

        long totalKb = kb.GetValueOrDefault("MemTotal", 0);
        long availableKb = kb.GetValueOrDefault("MemAvailable", kb.GetValueOrDefault("MemFree", totalKb));

        double totalGB = Math.Round(totalKb / (1024.0 * 1024.0), 1);
        double usedGB = Math.Round((totalKb - availableKb) / (1024.0 * 1024.0), 1);
        double percent = totalKb > 0 ? Math.Round((totalKb - availableKb) / (double)totalKb * 100, 0) : 0;

        var usage = new MemoryUsage(totalGB, usedGB, percent)
        {
            SwapText = FormatSwap(kb),
            ActiveText = FormatKb(kb, "Active"),
            InactiveText = FormatKb(kb, "Inactive"),
            SlabText = FormatKb(kb, "Slab")
        };
        return usage;
    }

    private static string? FormatSwap(Dictionary<string, long> kb)
    {
        long total = kb.GetValueOrDefault("SwapTotal", 0);
        long free = kb.GetValueOrDefault("SwapFree", 0);

        string baseText;
        if (total <= 0)
            baseText = "Not in use";
        else
        {
            long used = Math.Max(0, total - free);
            double pct = (double)used / total * 100;
            baseText = $"{Gb(used)} / {Gb(total)} ({pct:F0}%)";
        }

        string? zram = ReadZramText();
        return zram == null ? baseText : $"{baseText} · {zram}";
    }

    private static string? ReadZramText()
    {
        try
        {
            if (!File.Exists("/proc/swaps")) return null;

            string[] lines = File.ReadAllLines("/proc/swaps");
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int first = line.IndexOf(' ');
                if (first <= 0) continue;
                string dev = line[..first];
                string name = Path.GetFileName(dev);
                if (!name.StartsWith("zram", StringComparison.Ordinal)) continue;

                // /sys/block/zramN/mem_used_total is the actual compressed bytes in use.
                string usedPath = $"/sys/block/{name}/mem_used_total";
                if (!File.Exists(usedPath)) continue;
                if (!long.TryParse(File.ReadAllText(usedPath).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long usedBytes)) continue;

                long usedMb = usedBytes / (1024 * 1024);
                return usedMb >= 1024
                    ? $"ZRAM {usedMb / 1024.0:F1} GB used"
                    : $"ZRAM {usedMb} MB used";
            }
        }
        catch { }
        return null;
    }

    private static string? FormatKb(Dictionary<string, long> kb, string key)
    {
        if (!kb.TryGetValue(key, out long value)) return null;
        return Gb(value);
    }

    private static string Gb(long kb) => $"{kb / 1024.0 / 1024.0:F2} GB";

    private static Dictionary<string, long> ReadMemInfoKb()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string line in File.ReadAllLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line[..colon];
                string rest = line[(colon + 1)..].Trim();
                int space = rest.IndexOf(' ');
                string num = space > 0 ? rest[..space] : rest;
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                    map[key] = v;
            }
        }
        catch { }
        return map;
    }
}
