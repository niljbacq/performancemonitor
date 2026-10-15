using System;
using System.Globalization;
using System.IO;

namespace TaskManager.Providers;

/// <summary>
/// Linux Pressure Stall Information (/proc/pressure/*). PSI reports the share of
/// wall time in which at least one runnable task is stalled on a resource
/// ("some") and the share in which every non-idle task is stalled ("full").
/// Available since kernel 4.20 when CONFIG_PSI is enabled; returns null
/// everywhere else so the row is simply hidden.
/// </summary>
public static class LinuxPressure
{
    public static string? Cpu() => Format("cpu", includeFull: false);
    public static string? Memory() => Format("memory", includeFull: true);
    public static string? Io() => Format("io", includeFull: true);

    private static string? Format(string resource, bool includeFull)
    {
        try
        {
            string path = $"/proc/pressure/{resource}";
            if (!File.Exists(path)) return null;

            double? some10 = null, full10 = null;
            foreach (string line in File.ReadLines(path))
            {
                var (kind, avg10) = ParseLine(line);
                if (kind == "some") some10 = avg10;
                else if (kind == "full") full10 = avg10;
            }

            if (some10 == null) return null;

            string text = $"some {some10.Value:F2}% (10s avg)";
            if (includeFull && full10 != null)
                text += $", full {full10.Value:F2}%";
            return text;
        }
        catch { return null; }
    }

    // "some avg10=0.00 avg60=0.00 avg300=0.00 total=1234"
    private static (string Kind, double Avg10) ParseLine(string line)
    {
        int space = line.IndexOf(' ');
        if (space <= 0) return ("", 0);
        string kind = line[..space];
        double avg10 = 0;
        foreach (string token in line[(space + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("avg10=", StringComparison.Ordinal)) continue;
            double.TryParse(token[6..], NumberStyles.Float, CultureInfo.InvariantCulture, out avg10);
        }
        return (kind, avg10);
    }
}
