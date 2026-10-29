using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace TaskManager.Providers;

[SupportedOSPlatform("windows")]
public sealed class WindowsGpuProvider : IGpuProvider, IGpuListProvider
{
    private List<GpuInfo>? _cached;
    private readonly object _lock = new();

    public GpuSpecs GetSpecs()
    {
        var primary = GpuMap.Primary(GetGpuList());
        return primary == null ? new GpuSpecs() : GpuMap.ToSpecs(primary);
    }

    public GpuSample? Sample()
    {
        var primary = GpuMap.Primary(SampleGpuList());
        if (primary == null) return null;
        if (primary.Utilization == null && primary.TemperatureC == null && primary.MemoryUsage == null
            && primary.UtilizationText == null && primary.TemperatureText == null)
            return null;
        return GpuMap.ToSample(primary);
    }

    public IReadOnlyList<GpuInfo> GetGpuList()
    {
        lock (_lock)
        {
            _cached ??= BuildList();
            return _cached;
        }
    }

    public IReadOnlyList<GpuInfo> SampleGpuList()
    {
        var specs = GetGpuList();
        var rows = NvidiaSmi.Query();

        var result = new List<GpuInfo>(specs.Count);
        foreach (var spec in specs)
        {
            var info = GpuMap.Clone(spec);
            result.Add(info);
        }

        // NVIDIA rows: live utilization/temperature/memory matched by PCI bus.
        NvidiaSmi.Apply(result, rows, includeLive: true);

        // Non-NVIDIA (or when nvidia-smi is missing): GPU Engine 3D counters,
        // summed per phys_N adapter. Values are keyed by adapter index, so an
        // adapter that already has a value cannot shift the ones after it.
        GpuAssign.ApplyByIndex(result, SamplePerfByPhys(), SampleAdapterMemoryByPhys());

        // Temperature straight from the WDDM driver for adapters nvidia-smi
        // did not fill; null when the driver does not answer.
        GpuAssign.ApplyTemperatures(result, GpuTemperature.ReadAll());

        // Linux shows why there is no utilization; match that instead of "-".
        foreach (var g in result)
        {
            if (g.Utilization == null && g.UtilizationText == null)
                g.UtilizationText = GpuReason.ForUtilization(g.DriverName);
        }

        return result;
    }

    private List<GpuInfo> BuildList()
    {
        var list = new List<GpuInfo>();
        var vramTotals = ReadDisplayAdapterTotalBytes();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Generic GPU";
                if (GpuClassifier.IsIgnoredAdapter(name)) continue;

                string? pnp = obj["PNPDeviceID"]?.ToString();
                var info = new GpuInfo
                {
                    Name = name,
                    DriverVersion = obj["DriverVersion"]?.ToString(),
                    PhysicalLocation = pnp ?? "PCI Bus",
                    Vendor = VendorFromName(name),
                    Kind = GpuClassifier.Classify(name, pnp),
                    TotalMemoryBytes = MatchTotalBytes(vramTotals, pnp)
                };
                if (DateTime.TryParse(obj["DriverDate"]?.ToString(), out var date))
                    info.DriverDate = date.ToShortDateString();
                info.DriverName = ReadDriverService(pnp);
                list.Add(info);
            }
        }
        catch
        {
            // WMI unavailable; fall through to the placeholder below.
        }

        if (list.Count == 0)
        {
            list.Add(new GpuInfo
            {
                Name = "Windows Graphics Device",
                Kind = "Unknown"
            });
        }

        // Enrich NVIDIA specs (name, driver, PCI bus) without live values.
        NvidiaSmi.Apply(list, NvidiaSmi.Query(), includeLive: false);
        return list;
    }

    // Kernel miniport service name ("amdkmdag", "igdkmd64", "nvlddmkm") from the
    // device's enum key; the WMI InstalledDisplayDrivers value holds DLL paths.
    private static string? ReadDriverService(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnpDeviceId);
            return key?.GetValue("Service") as string;
        }
        catch
        {
            return null;
        }
    }

    // WMI formatted GPU perf classes keep English names on non-English Windows
    // (the "GPU Engine" / "GPU Adapter Memory" PerformanceCounter categories do not).
    private SortedDictionary<int, float> SamplePerfByPhys()
    {
        var byPhys = new SortedDictionary<int, float>();
        try
        {
            var instances = new List<(string Name, float Usage)>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                var rawUsage = obj["UtilizationPercentage"];
                var usage = (rawUsage == null || rawUsage == DBNull.Value) ? 0f : (float)Convert.ToDouble(rawUsage);
                instances.Add((name, usage));

                System.Diagnostics.Debug.WriteLine($"[GPU WMI] Name='{name}', UtilizationPercentage={rawUsage}");
            }

            bool has3D = instances.Any(i => i.Name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));
            bool useCompute = !has3D && instances.Any(i => i.Name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase));

            foreach (var instance in instances)
            {
                bool is3D = instance.Name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase);
                bool isCompute = instance.Name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase);

                if (has3D ? !is3D : (!useCompute || !isCompute)) continue;

                int? phys = PhysIndex(instance.Name);
                if (phys == null) continue;

                byPhys[phys.Value] = byPhys.TryGetValue(phys.Value, out float sum)
                    ? sum + instance.Usage
                    : instance.Usage;
            }
        }
        catch { }

        foreach (int key in byPhys.Keys.ToList())
            byPhys[key] = (float)Math.Min(Math.Round(byPhys[key], 1), 100);
        return byPhys;
    }

    private static int? PhysIndex(string instance)
    {
        int idx = instance.IndexOf("phys_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int start = idx + 5;
        int end = start;
        while (end < instance.Length && char.IsDigit(instance[end])) end++;
        if (end == start) return null;
        return int.TryParse(instance.AsSpan(start, end - start), out int v) ? v : null;
    }

    private SortedDictionary<int, (long Dedicated, long Shared)> SampleAdapterMemoryByPhys()
    {
        var byPhys = new SortedDictionary<int, (long, long)>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                int? phys = PhysIndex(name);
                if (phys == null) continue;

                long dedicated = Convert.ToInt64(obj["DedicatedUsage"]);
                long shared = Convert.ToInt64(obj["SharedUsage"]);
                byPhys[phys.Value] = byPhys.TryGetValue(phys.Value, out var sum)
                    ? (sum.Item1 + dedicated, sum.Item2 + shared)
                    : (dedicated, shared);
            }
        }
        catch { }
        return byPhys;
    }

    // Display-adapter total VRAM from the registry. Win32_VideoController.AdapterRAM
    // is a uint32 and saturates at 4 GB; HardwareInformation.qwMemorySize is a
    // 64-bit REG_BINARY under the display class key.
    private static Dictionary<string, long> ReadDisplayAdapterTotalBytes()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            if (root == null) return result;
            foreach (string name in root.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                using var sub = root.OpenSubKey(name);
                if (sub == null) continue;
                if (sub.GetValue("MatchingDeviceId") is not string match || match.Length == 0) continue;
                long total = ReadQword(sub, "HardwareInformation.qwMemorySize");
                if (total > 0) result[match] = total;
            }
        }
        catch { }
        return result;
    }

    private static long ReadQword(RegistryKey key, string valueName)
    {
        try
        {
            object? v = key.GetValue(valueName);
            if (v is byte[] bytes && bytes.Length >= 8) return BitConverter.ToInt64(bytes, 0);
            if (v is long l) return l;
            if (v is int i) return i;
        }
        catch { }
        return 0;
    }

    // Match a WMI PNPDeviceID against a display-class MatchingDeviceId. The class
    // key stores "PCI\VEN_1002&DEV_130F&REV_D4" while WMI reports
    // "PCI\VEN_1002&DEV_130F&SUBSYS_..&REV_D4\..", which diverges at SUBSYS, so
    // after the exact-prefix check fall back to the vendor+device prefix.
    public static long? MatchTotalBytes(Dictionary<string, long> totals, string? pnp)
    {
        if (string.IsNullOrEmpty(pnp) || totals.Count == 0) return null;
        string norm = pnp.ToUpperInvariant();
        foreach (var kv in totals)
        {
            string m = kv.Key.ToUpperInvariant();
            if (norm.StartsWith(m, StringComparison.Ordinal) || m.StartsWith(norm, StringComparison.Ordinal))
                return kv.Value;
        }

        string key = VendorDeviceKey(norm);
        if (key.Length == 0) return null;
        foreach (var kv in totals)
        {
            if (VendorDeviceKey(kv.Key.ToUpperInvariant()) == key) return kv.Value;
        }
        return null;
    }

    private static string VendorDeviceKey(string id)
    {
        int dev = id.IndexOf("&DEV_", StringComparison.Ordinal);
        return dev > 0 && dev + 9 <= id.Length ? id[..(dev + 9)] : "";
    }

    private static string? VendorFromName(string name)
    {
        if (GpuClassifier.IsNvidia(name, null)) return "NVIDIA";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ATI", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        return null;
    }
}

internal static class GpuClassifier
{
    public static bool IsIgnoredAdapter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return Has(name, "Microsoft Basic Display")
            || Has(name, "Microsoft Remote Display")
            || Has(name, "Remote Desktop")
            || Has(name, "Indirect Display")
            || Has(name, "IddCx")
            || Has(name, "Virtual Display")
            || Has(name, "Mirror Driver")
            || Has(name, "Parsec")
            || Has(name, "VirtualBox")
            || Has(name, "Hyper-V");
    }

    public static bool IsNvidia(string? name, string? vendorHint)
        => Has(name, "NVIDIA") || Has(name, "GeForce") || Has(name, "Quadro") || Has(name, "Tesla")
           || Has(vendorHint, "NVIDIA") || HasVendor(vendorHint, "10de");

    public static string Classify(string? name, string? vendorHint)
    {
        string n = name ?? "";
        if (IsNvidia(n, vendorHint)) return "Dedicated";
        if (IsIntel(n, vendorHint)) return IsDiscreteArc(n) ? "Dedicated" : "Integrated";
        if (IsAmd(n, vendorHint))
        {
            if (IsAmdDiscrete(n)) return "Dedicated";
            if (IsAmdIntegrated(n)) return "Integrated";
            // Fallback names ("AMD GPU (0x130f)") carry no model info; when lspci
            // could not be matched, assume an AMD iGPU (APU) classification.
            return "Integrated";
        }
        if (Has(n, "Mali") || Has(n, "Adreno") || Has(n, "PowerVR") || Has(n, "Immortalis") || Has(n, "Xclipse"))
            return "Integrated";
        return "Unknown";
    }

    private static bool IsIntel(string name, string? vendorHint)
        => Has(name, "Intel") || Has(vendorHint, "Intel") || HasVendor(vendorHint, "8086");

    private static bool IsAmd(string name, string? vendorHint)
        => Has(name, "AMD") || Has(name, "Radeon") || Has(name, "ATI")
           || Has(vendorHint, "AMD") || Has(vendorHint, "ATI")
           || HasVendor(vendorHint, "1002") || HasVendor(vendorHint, "1022");

    // Desktop Arc has a model number (A770, B580). "Arc Graphics" on Core Ultra does not.
    private static bool IsDiscreteArc(string name)
    {
        int i = name.IndexOf("Arc", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        string rest = name[(i + 3)..]
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("™", "");
        int j = 0;
        while (j < rest.Length && !char.IsLetter(rest[j])) j++;
        if (j + 1 >= rest.Length) return false;
        char c = char.ToUpperInvariant(rest[j]);
        return (c == 'A' || c == 'B') && char.IsDigit(rest[j + 1]);
    }

    private static bool IsAmdDiscrete(string name)
    {
        if (HasRxModel(name) || Has(name, "RX Vega M")) return true;
        return HasToken(name, "Navi") || HasToken(name, "Polaris") || HasToken(name, "Ellesmere")
            || HasToken(name, "Fiji") || HasToken(name, "Hawaii") || HasToken(name, "Tonga")
            || HasToken(name, "Vega 10") || HasToken(name, "Vega 20")
            || HasToken(name, "FirePro") || HasToken(name, "Instinct") || HasToken(name, "Radeon Pro");
    }

    private static bool IsAmdIntegrated(string name)
    {
        if (HasToken(name, "Vega 3") || HasToken(name, "Vega 6") || HasToken(name, "Vega 7")
            || HasToken(name, "Vega 8") || HasToken(name, "Vega 11"))
            return true;
        if (HasToken(name, "Raven") || HasToken(name, "Picasso") || HasToken(name, "Renoir")
            || HasToken(name, "Cezanne") || HasToken(name, "Lucienne") || HasToken(name, "Barcelo")
            || HasToken(name, "Rembrandt") || HasToken(name, "Phoenix") || HasToken(name, "Hawk Point")
            || HasToken(name, "Strix") || HasToken(name, "Mendocino") || HasToken(name, "Van Gogh")
            || HasToken(name, "Raphael") || HasToken(name, "Granite Ridge"))
            return true;
        return Has(name, "Graphics");
    }

    private static bool HasRxModel(string name)
    {
        int i = 0;
        while ((i = name.IndexOf("RX", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int j = i + 2;
            while (j < name.Length && (name[j] == ' ' || name[j] == '-')) j++;
            if (j < name.Length && char.IsDigit(name[j])) return true;
            i += 2;
        }
        return false;
    }

    private static bool HasToken(string text, string token)
    {
        int i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = i + token.Length;
            bool beforeOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            bool afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (beforeOk && afterOk) return true;
            i += token.Length;
        }
        return false;
    }

    private static bool Has(string? text, string token)
        => !string.IsNullOrEmpty(text) && text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool HasVendor(string? text, string hex)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains("VEN_" + hex, StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("0x" + hex, StringComparison.OrdinalIgnoreCase)) return true;
        return text.Trim().Equals(hex, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class NvidiaGpuRow
{
    public string Name { get; init; } = "";
    public string? DriverVersion { get; init; }
    public string? PciBus { get; init; }
    public double? Utilization { get; init; }
    public double? TemperatureC { get; init; }
    public string? MemoryUsage { get; init; }
}

internal static class NvidiaSmi
{
    public static IReadOnlyList<NvidiaGpuRow> Query()
    {
        string raw = CommandRunner.Run(
            "nvidia-smi",
            "--query-gpu=name,driver_version,pci.bus_id,utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits",
            2000);
        var list = new List<NvidiaGpuRow>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var line in raw.Split('\n'))
        {
            var row = Parse(line.Trim());
            if (row != null) list.Add(row);
        }
        return list;
    }

    public static void Apply(IReadOnlyList<GpuInfo> gpus, IReadOnlyList<NvidiaGpuRow> rows, bool includeLive)
    {
        if (rows.Count == 0) return;
        var nvidia = new List<GpuInfo>();
        foreach (var g in gpus)
        {
            if (GpuClassifier.IsNvidia(g.Name, g.Vendor))
                nvidia.Add(g);
        }
        var used = new bool[rows.Count];
        foreach (var g in nvidia)
        {
            int idx = -1;
            if (!string.IsNullOrEmpty(g.PciBus))
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    if (used[r]) continue;
                    if (string.Equals(g.PciBus, rows[r].PciBus, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = r;
                        break;
                    }
                }
            }
            if (idx < 0)
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    if (!used[r]) { idx = r; break; }
                }
            }
            if (idx < 0) continue;
            used[idx] = true;
            var row = rows[idx];
            if (!string.IsNullOrWhiteSpace(row.Name)) g.Name = row.Name;
            if (!string.IsNullOrWhiteSpace(row.DriverVersion)) g.DriverVersion = row.DriverVersion;
            if (!string.IsNullOrWhiteSpace(row.PciBus)) g.PciBus = row.PciBus;
            if (!includeLive) continue;
            if (row.Utilization is double u) { g.Utilization = u; g.UtilizationText = null; }
            if (row.TemperatureC is double t) { g.TemperatureC = t; g.TemperatureText = null; }
            if (row.MemoryUsage != null) g.MemoryUsage = row.MemoryUsage;
        }
    }

    private static NvidiaGpuRow? Parse(string line)
    {
        if (line.Length == 0) return null;
        var parts = line.Split(',');
        if (parts.Length < 7) return null;
        int extra = parts.Length - 7;
        var nameParts = new string[1 + extra];
        Array.Copy(parts, nameParts, nameParts.Length);
        string name = string.Join(",", nameParts).Trim();
        if (name.Length == 0) return null;
        string used = parts[5 + extra].Trim();
        string total = parts[6 + extra].Trim();
        string? memory = null;
        if (TryInvariant(used, out _) && TryInvariant(total, out _))
            memory = $"{used} MB / {total} MB";
        string driver = parts[1 + extra].Trim();
        string bus = GpuPci.Normalize(parts[2 + extra].Trim());
        return new NvidiaGpuRow
        {
            Name = name,
            DriverVersion = driver.Length == 0 ? null : driver,
            PciBus = bus.Length == 0 ? null : bus,
            Utilization = TryInvariant(parts[3 + extra], out double util) ? util : null,
            TemperatureC = TryInvariant(parts[4 + extra], out double temp) ? temp : null,
            MemoryUsage = memory
        };
    }

    private static bool TryInvariant(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

internal static class GpuPci
{
    public static string Normalize(string bus)
    {
        var parts = bus.Trim().ToLowerInvariant().Split(':');
        if (parts.Length != 3) return bus.Trim().ToLowerInvariant();
        if (!int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int domain))
            return bus.Trim().ToLowerInvariant();
        return $"{domain:x4}:{parts[1].PadLeft(2, '0')}:{parts[2]}";
    }
}

internal static class GpuMap
{
    public static GpuSpecs ToSpecs(GpuInfo g) => new()
    {
        Name = g.Name,
        DriverVersion = g.DriverVersion,
        DriverDate = g.DriverDate,
        GraphicsApi = g.GraphicsApi,
        PhysicalLocation = g.PhysicalLocation,
        Vendor = g.Vendor
    };

    public static GpuSample ToSample(GpuInfo g) => new()
    {
        Utilization = g.Utilization,
        TemperatureC = g.TemperatureC,
        MemoryUsage = g.MemoryUsage,
        UtilizationText = g.UtilizationText,
        TemperatureText = g.TemperatureText
    };

    public static GpuInfo Clone(GpuInfo src) => new()
    {
        DeviceId = src.DeviceId,
        PciBus = src.PciBus,
        Name = src.Name,
        Kind = src.Kind,
        DriverVersion = src.DriverVersion,
        DriverDate = src.DriverDate,
        DriverName = src.DriverName,
        GraphicsApi = src.GraphicsApi,
        PhysicalLocation = src.PhysicalLocation,
        Vendor = src.Vendor,
        Utilization = src.Utilization,
        TemperatureC = src.TemperatureC,
        UtilizationText = src.UtilizationText,
        TemperatureText = src.TemperatureText,
        MemoryUsage = src.MemoryUsage,
        SharedMemoryUsage = src.SharedMemoryUsage,
        TotalMemoryBytes = src.TotalMemoryBytes
    };

    public static GpuInfo? Primary(IReadOnlyList<GpuInfo> list)
    {
        foreach (var g in list)
        {
            if (g.Kind == "Dedicated") return g;
        }
        return list.Count > 0 ? list[0] : null;
    }
}

// Assigns per-adapter counters by adapter index. A running counter (the old
// behaviour) shifted every later adapter whenever an earlier one was skipped.
public static class GpuAssign
{
    public static void ApplyByIndex(IReadOnlyList<GpuInfo> gpus,
        SortedDictionary<int, float> utilization,
        SortedDictionary<int, (long Dedicated, long Shared)> memory)
    {
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            if (g.Utilization == null && utilization.TryGetValue(i, out float u))
                g.Utilization = u;

            if (g.MemoryUsage != null || !memory.TryGetValue(i, out var m)) continue;
            if (m.Dedicated <= 0 && m.Shared <= 0 && g.TotalMemoryBytes == null) continue;

            string used = Bytes(m.Dedicated);
            g.MemoryUsage = g.TotalMemoryBytes is long total ? $"{used} / {Bytes(total)}" : used;
            if (m.Shared > 0) g.SharedMemoryUsage = Bytes(m.Shared);
        }
    }

    public static void ApplyTemperatures(IReadOnlyList<GpuInfo> gpus, IReadOnlyList<double?> temps)
    {
        // A shifted list would show one card's temperature on another, so a
        // length mismatch applies nothing and the rows stay hidden.
        if (temps.Count != gpus.Count) return;

        for (int i = 0; i < gpus.Count; i++)
        {
            if (gpus[i].TemperatureC == null && gpus[i].TemperatureText == null
                && temps[i] is double t)
                gpus[i].TemperatureC = t;
        }
    }

    public static string Bytes(long b) =>
        b >= 1024L * 1024 * 1024 ? $"{b / (1024.0 * 1024 * 1024):F2} GB"
        : b >= 1024L * 1024 ? $"{b / (1024.0 * 1024):F0} MB"
        : $"{b / 1024.0:F0} KB";
}

public sealed class LinuxGpuProvider : IGpuProvider, IGpuListProvider
{
    private const string DrmRoot = "/sys/class/drm";
    private List<GpuInfo>? _cached;
    private readonly object _lock = new();

    // Per-GPU accumulated engine-busy baselines for the computed-utilization path
    // (deltas of /proc/<pid>/fdinfo drm-engine-* sums over the sample window).
    private readonly Dictionary<string, (DateTime At, long BusyNs)> _busyBaseline = new();

    // Legacy radeon has no fdinfo engine counters. Its GPU-busy register is
    // readable unprivileged through the DRM render node, so keep one sampler
    // per render node instead of depending on radeontop.
    private readonly Dictionary<string, RadeonLoadSampler> _radeonSamplers = new();

    public GpuSpecs GetSpecs()
    {
        var primary = GpuMap.Primary(GetGpuList());
        return primary == null ? new GpuSpecs() : GpuMap.ToSpecs(primary);
    }

    public GpuSample? Sample()
    {
        var primary = GpuMap.Primary(SampleGpuList());
        if (primary == null) return null;
        if (primary.Utilization == null && primary.TemperatureC == null && primary.MemoryUsage == null
            && primary.UtilizationText == null && primary.TemperatureText == null)
            return null;
        return GpuMap.ToSample(primary);
    }

    public IReadOnlyList<GpuInfo> GetGpuList()
    {
        lock (_lock)
        {
            _cached ??= BuildList();
            return _cached;
        }
    }

    public IReadOnlyList<GpuInfo> SampleGpuList()
    {
        var specs = GetGpuList();
        var rows = NvidiaSmi.Query();
        var result = new List<GpuInfo>(specs.Count);
        foreach (var spec in specs)
        {
            var info = GpuMap.Clone(spec);
            try { FillSysfsSample(info); } catch { }
            try { FillComputedUtilization(info); } catch { }
            result.Add(info);
        }
        NvidiaSmi.Apply(result, rows, includeLive: true);
        return result;
    }

    // Computed GPU utilization (the "we have the total, we compute the %" path):
    // modern amdgpu/i915 drivers publish per-process engine-busy time in
    // /proc/<pid>/fdinfo (drm-engine-<engine>=<ns>). Summing deltas across all
    // processes over the sample window and dividing by elapsed wall time gives a
    // real utilization figure without any direct device read. When no engine data
    // exists at all (radeon and other legacy drivers), we do not substitute CPU %.
    private void FillComputedUtilization(GpuInfo info)
    {
        string? pci = info.PciBus;
        if (string.IsNullOrEmpty(pci)) return;

        string key = GpuPci.Normalize(pci);
        var (busyNs, sawEngineData) = SumFdinfoEngineNs(pci);
        DateTime now = DateTime.UtcNow;

        if (_busyBaseline.TryGetValue(key, out var prev))
        {
            double elapsedMs = (now - prev.At).TotalMilliseconds;
            if (elapsedMs >= 100)
            {
                if (sawEngineData)
                {
                    // Real per-process GPU busy time: genuine utilization.
                    double busyMs = (busyNs - prev.BusyNs) / 1_000_000.0;
                    double util = Math.Clamp(busyMs / elapsedMs * 100.0, 0, 100);
                    info.Utilization = Math.Round(util, 1);
                    info.UtilizationText = null;
                }
                else if (info.Utilization == null)
                {
                    bool radeon = info.DriverVersion != null &&
                        info.DriverVersion.StartsWith("radeon", StringComparison.OrdinalIgnoreCase);
                    if (radeon && !IsRadeonWarming(info))
                        info.UtilizationText = RadeonUtilReason();
                }
            }
        }

        _busyBaseline[key] = (now, busyNs);
    }

    private static string RadeonUtilReason()
        => "radeon GPU load unavailable (DRM render node inaccessible or unsupported)";

    // drm-engine-* totals in fdinfo are grouped under a "drm-pdev: <pci slot>" header
    // for each open GPU fd. We sum engine ns only for the block whose drm-pdev matches
    // this GPU's PCI slot. Returns (total busy ns, whether any engine data was seen).
    private static (long TotalNs, bool AnyData) SumFdinfoEngineNs(string pci)
    {
        string want = GpuPci.Normalize(pci);
        long total = 0;
        bool any = false;
        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                string pid = Path.GetFileName(procDir);
                if (!int.TryParse(pid, out _)) continue;

                string fdinfoDir = Path.Combine(procDir, "fdinfo");
                if (!Directory.Exists(fdinfoDir)) continue;

                try
                {
                    foreach (string fdFile in Directory.EnumerateFiles(fdinfoDir))
                    {
                        bool inBlock = false;
                        foreach (string line in File.ReadLines(fdFile))
                        {
                            int colon = line.IndexOf(':');
                            if (colon <= 0) continue;
                            string key = line[..colon].Trim();
                            string value = line[(colon + 1)..].Trim();

                            if (key == "drm-pdev")
                            {
                                inBlock = GpuPci.Normalize(value) == want;
                                continue;
                            }

                            if (inBlock && key.StartsWith("drm-engine-", StringComparison.Ordinal))
                            {
                                any = true;
                                // fdinfo writes "<nanoseconds> ns"; parse only the number.
                                int space = value.IndexOf(' ');
                                string number = space > 0 ? value[..space] : value;
                                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ns))
                                    total += ns;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return (total, any);
    }

    private List<GpuInfo> BuildList()
    {
        var lspci = ReadLspci();
        var bySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lspci)
            bySlot[entry.Slot] = entry.Name;

        var found = new List<(int Index, GpuInfo Info)>();
        if (Directory.Exists(DrmRoot))
        {
            foreach (var dir in Directory.GetDirectories(DrmRoot))
            {
                string card = Path.GetFileName(dir);
                if (!IsCardName(card, out int index)) continue;
                string device = Path.Combine(dir, "device");
                if (IsFramebufferDriver(device)) continue;

                string? vendorRaw = ReadTrimmed(Path.Combine(device, "vendor"));
                string? pci = PciSlot(device);
                string? slot = pci == null ? null : GpuPci.Normalize(pci);
                string vendor = VendorLabel(vendorRaw);
                string? name = null;
                if (slot != null && bySlot.TryGetValue(slot, out var fromPci))
                    name = fromPci;
                if (string.IsNullOrWhiteSpace(name))
                    name = FallbackName(vendor, ReadTrimmed(Path.Combine(device, "device")));

                string? driverVersion = ReadDriverVersion(device);

                found.Add((index, new GpuInfo
                {
                    DeviceId = card,
                    PciBus = slot,
                    Name = name,
                    Kind = GpuClassifier.Classify(name, vendorRaw),
                    Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor,
                    PhysicalLocation = slot ?? card,
                    DriverVersion = driverVersion
                }));
            }
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        var list = new List<GpuInfo>(found.Count);
        foreach (var item in found)
            list.Add(item.Info);

        if (list.Count == 0)
        {
            foreach (var entry in lspci)
            {
                list.Add(new GpuInfo
                {
                    Name = entry.Name,
                    PciBus = entry.Slot,
                    Kind = GpuClassifier.Classify(entry.Name, null),
                    Vendor = VendorLabelFromName(entry.Name),
                    PhysicalLocation = entry.Slot
                });
            }
        }

        NvidiaSmi.Apply(list, NvidiaSmi.Query(), includeLive: false);

        // Start the radeon register sampler as soon as the GPU list is known,
        // so the first GPU sample usually has a real value already.
        foreach (var gpu in list)
            _ = GetRadeonSampler(gpu);

        return list;
    }

    private void FillSysfsSample(GpuInfo info)
    {
        if (string.IsNullOrEmpty(info.DeviceId)) return;
        string device = Path.Combine(DrmRoot, info.DeviceId, "device");

        string busyPath = Path.Combine(device, "gpu_busy_percent");
        if (TryReadDouble(busyPath, out double busy))
            info.Utilization = busy;
        else if (!TryRadeonRegisterUtilization(info) && !TryDebugfsLoad(info))
        {
            // Fall back to the external radeontop tool only if the in-process
            // DRM reader and debugfs both failed.
            if (!TryRadeontopUtilization(info))
                info.UtilizationText = DebugfsReason(device);
        }

        if (TryReadTempC(device, out double temp))
            info.TemperatureC = temp;

        string? vram = FormatBytePair(
            ReadLong(Path.Combine(device, "mem_info_vram_used")),
            ReadLong(Path.Combine(device, "mem_info_vram_total")));
        string? gtt = FormatBytePair(
            ReadLong(Path.Combine(device, "mem_info_gtt_used")),
            ReadLong(Path.Combine(device, "mem_info_gtt_total")));

        // Legacy radeon has no mem_info sysfs files; its sampler reads VRAM and
        // GTT usage through the same render node it uses for GPU load.
        if (vram == null && ReadDriverName(device) == "radeon")
        {
            var sampler = GetRadeonSampler(info);
            if (sampler is { } s)
            {
                var (radeonVram, radeonGtt) = s.Memory;
                if (radeonVram != null)
                {
                    vram = radeonVram;
                    gtt ??= radeonGtt;
                }
                else if (!s.Warming)
                {
                    info.MemoryUsage =
                        "radeon GPU memory unavailable (DRM render node inaccessible or unsupported)";
                }
            }
            else
            {
                info.MemoryUsage =
                    "radeon GPU memory unavailable (DRM render node inaccessible or unsupported)";
            }
        }

        if (vram != null) info.MemoryUsage = vram;
        if (gtt != null) info.SharedMemoryUsage = gtt;
    }

    private bool TryRadeonRegisterUtilization(GpuInfo info)
    {
        var sampler = GetRadeonSampler(info);
        if (sampler == null) return false;
        if (sampler.Percent is double pct)
        {
            info.Utilization = pct;
            info.UtilizationText = null;
            return true;
        }
        // The first one-second average is still being collected. Treat that as
        // handled so the UI stays blank instead of briefly claiming the node is
        // inaccessible.
        return sampler.Warming;
    }

    private bool IsRadeonWarming(GpuInfo info)
    {
        string? node = FindRenderNode(info.DeviceId);
        if (node == null) return false;
        lock (_lock)
            return _radeonSamplers.TryGetValue(node, out var sampler) && sampler.Warming;
    }

    private RadeonLoadSampler? GetRadeonSampler(GpuInfo info)
    {
        if (info.DriverVersion == null ||
            !info.DriverVersion.StartsWith("radeon", StringComparison.OrdinalIgnoreCase))
            return null;

        string? node = FindRenderNode(info.DeviceId);
        if (node == null) return null;

        lock (_lock)
        {
            if (!_radeonSamplers.TryGetValue(node, out var sampler))
            {
                sampler = new RadeonLoadSampler(node);
                _radeonSamplers[node] = sampler;
                sampler.Start();
            }
            return sampler;
        }
    }

    private static string? FindRenderNode(string? card)
    {
        if (string.IsNullOrEmpty(card)) return null;
        try
        {
            string dir = Path.Combine(DrmRoot, card, "device", "drm");
            if (!Directory.Exists(dir)) return null;
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                string name = Path.GetFileName(entry);
                if (name.StartsWith("renderD", StringComparison.Ordinal))
                    return Path.Combine("/dev/dri", name);
            }
        }
        catch { }
        return null;
    }

    private static bool TryDebugfsLoad(GpuInfo info)
    {
        if (info.DeviceId == null || !int.TryParse(info.DeviceId.AsSpan(4), out int index)) return false;
        string[] paths =
        {
            $"/sys/kernel/debug/dri/{index}/amdgpu_pm_info",
            $"/sys/kernel/debug/dri/{index}/radeon_pm_info"
        };
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            bool foundLoad = false;
            foreach (string line in File.ReadAllLines(path))
            {
                if (!line.Contains("GPU Load", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("GPU load", StringComparison.Ordinal)) continue;
                foreach (string token in line.Split(new[] { ':', '%', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        && v is >= 0 and <= 100)
                    {
                        info.Utilization = v;
                        info.UtilizationText = null;
                        foundLoad = true;
                        break;
                    }
                }
                if (foundLoad) break;
            }

            // radeon_pm_info on Kaveri has sclk/mclk lines — read them as an honest clock row
            if (!foundLoad && path.Contains("radeon_pm_info"))
            {
                ReadRadeonClocks(path, info);
            }

            if (!foundLoad)
            {
                info.UtilizationText = $"{Path.GetFileName(path)} has no GPU load counter";
            }
            return true;
        }
        return false;
    }

    private static bool? _radeontopAvailable;

    // Last-resort utilization for radeon: radeontop reads the GPU busy register
    // over DRM, so it works without debugfs/root. It is a separate binary, so we
    // remember when it is missing to avoid respawning it every sample.
    private static bool TryRadeontopUtilization(GpuInfo info)
    {
        if (_radeontopAvailable == false) return false;

        string raw = CommandRunner.Run("radeontop", "-d - -l 1", 2500);
        if (string.IsNullOrWhiteSpace(raw))
        {
            _radeontopAvailable = false;
            return false;
        }
        _radeontopAvailable = true;

        // Dump mode ("-d -") writes "gpu 12.34%"; the interactive line is
        // "720.06MHz, 12.34% GPU, ...". Accept either.
        var m = Regex.Match(raw, @"gpu\s+(\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(raw, @"(\d+(?:\.\d+)?)\s*%\s*GPU", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            return false;

        double p = Math.Clamp(pct, 0, 100);
        info.Utilization = p;
        info.UtilizationText = $"{Math.Round(p, 1)}% (radeontop)";
        return true;
    }

    private static void ReadRadeonClocks(string path, GpuInfo info)
    {
        try
        {
            long? sclk = null, mclk = null;
            foreach (string line in File.ReadAllLines(path))
            {
                // sclk: 72000 kHz (or similar format)
                // mclk: 80000 kHz
                string[] parts = line.Split(new[] { ':', '=', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("sclk", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(parts[i + 1], out long s))
                        sclk = s;
                    else if (parts[i].Equals("mclk", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(parts[i + 1], out long m))
                        mclk = m;
                }
            }
            if (sclk.HasValue || mclk.HasValue)
            {
                info.UtilizationText = $"GPU clock: {(sclk ?? 0) / 1000} / {(mclk ?? 0) / 1000} MHz";
            }
        }
        catch { }
    }

    private static string DebugfsReason(string device)
    {
        string driver = ReadDriverName(device) ?? "this";
        bool mounted;
        try
        {
            mounted = Directory.Exists("/sys/kernel/debug")
                && Directory.EnumerateFileSystemEntries("/sys/kernel/debug").Any();
        }
        catch (UnauthorizedAccessException)
        {
            // debugfs is mounted but not listable by this user. This is not an
            // error for the caller; it only means the reason string differs.
            mounted = true;
        }
        if (!mounted)
            return "Requires debugfs mount for " + driver + " utilization (sudo mount -t debugfs none /sys/kernel/debug)";
        return driver == "radeon"
            ? "radeon GPU load unavailable (DRM render node inaccessible or unsupported)"
            : $"Utilization not exposed by the {driver} driver";
    }

    private static string? ReadDriverName(string device)
    {
        try { return Directory.ResolveLinkTarget(Path.Combine(device, "driver"), true)?.Name; }
        catch { return null; }
    }

    private readonly record struct LspciGpu(string Slot, string Name);

    private static List<LspciGpu> ReadLspci()
    {
        var list = new List<LspciGpu>();
        string raw = CommandRunner.Run("lspci", "-D");
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var line in raw.Split('\n'))
        {
            if (!(line.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase)
                || line.Contains("3D controller", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Display controller", StringComparison.OrdinalIgnoreCase)))
                continue;
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            string? name = NameFromLspci(line);
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new LspciGpu(GpuPci.Normalize(line[..space].Trim()), name));
        }
        return list;
    }

    private static string? NameFromLspci(string line)
    {
        const string marker = "controller:";
        int idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string? name = idx >= 0 ? line[(idx + marker.Length)..].Trim() : null;
        if (name == null)
        {
            int colon = line.LastIndexOf(':');
            if (colon < 0 || colon + 1 >= line.Length) return null;
            name = line[(colon + 1)..].Trim();
        }
        // lspci appends " (rev 09)"; it is noise for a display name.
        return Regex.Replace(name, @"\s*\(rev [0-9a-fA-F]+\)$", "");
    }

    private static bool IsCardName(string name, out int index)
    {
        index = -1;
        if (!name.StartsWith("card", StringComparison.Ordinal)) return false;
        return int.TryParse(name.AsSpan(4), out index);
    }

    // Driver version from sysfs: resolve the driver symlink (e.g. "radeon",
    // "amdgpu", "i915") and append the module version when exposed.
    private static string? ReadDriverVersion(string devicePath)
    {
        try
        {
            string? driver = Directory.ResolveLinkTarget(Path.Combine(devicePath, "driver"), true)?.Name;
            if (string.IsNullOrWhiteSpace(driver)) return null;

            // Prefer the driver's own version attribute when present.
            string verAttr = Path.Combine(devicePath, "driver", "version");
            if (File.Exists(verAttr))
            {
                string? v = ReadTrimmed(verAttr);
                if (!string.IsNullOrWhiteSpace(v)) return $"{driver} {v}";
            }

            // Fall back to the kernel module version (srcversion/module version).
            string modVer = $"/sys/module/{driver}/version";
            string? mv = ReadTrimmed(modVer);
            if (!string.IsNullOrWhiteSpace(mv)) return $"{driver} {mv}";

            // No version exposed — report the driver name only.
            return driver;
        }
        catch { return null; }
    }

    private static bool IsFramebufferDriver(string devicePath)
    {
        try
        {
            string? name = Directory.ResolveLinkTarget(Path.Combine(devicePath, "driver"), true)?.Name;
            return name is "simpledrm" or "efifb" or "vesafb" or "simple-framebuffer";
        }
        catch
        {
            return false;
        }
    }

    private static string? PciSlot(string devicePath)
    {
        try
        {
            // /sys/class/drm/cardN/device is a symlink chain that ends at the
            // PCI device directory (e.g. .../0000:00:01.0). Path.GetFullPath
            // does NOT resolve symlinks, so resolve the full link chain and use
            // the resolved target's directory name as the PCI slot.
            string full;
            try
            {
                var resolved = Directory.ResolveLinkTarget(devicePath, returnFinalTarget: true);
                full = resolved?.FullName ?? Path.GetFullPath(devicePath);
            }
            catch
            {
                full = Path.GetFullPath(devicePath);
            }

            string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Contains(':') && name.Contains('.')) return name;
        }
        catch { }
        return null;
    }

    private static string VendorLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string t = raw.Trim().ToLowerInvariant();
        if (t is "0x8086" or "8086") return "Intel";
        if (t is "0x10de" or "10de") return "NVIDIA";
        if (t is "0x1002" or "1002" or "0x1022" or "1022") return "AMD";
        return "";
    }

    private static string? VendorLabelFromName(string name)
    {
        if (GpuClassifier.IsNvidia(name, null)) return "NVIDIA";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("ATI", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        return null;
    }

    private static string FallbackName(string vendor, string? deviceId)
    {
        string who = string.IsNullOrWhiteSpace(vendor) ? "GPU" : vendor + " GPU";
        if (string.IsNullOrWhiteSpace(deviceId)) return who;
        return $"{who} ({deviceId.Trim()})";
    }

    private static bool TryReadDouble(string path, out double value)
    {
        value = 0;
        string? text = ReadTrimmed(path);
        return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadTempC(string devicePath, out double celsius)
    {
        celsius = 0;
        string hwmon = Path.Combine(devicePath, "hwmon");
        if (!Directory.Exists(hwmon)) return false;
        foreach (var dir in Directory.GetDirectories(hwmon))
        {
            if (TryReadDouble(Path.Combine(dir, "temp1_input"), out double milli))
            {
                celsius = milli / 1000.0;
                return true;
            }
        }
        return false;
    }

    private static long? ReadLong(string path)
    {
        string? text = ReadTrimmed(path);
        return text != null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    private static string? FormatBytePair(long? used, long? total)
    {
        if (used == null || total == null || total <= 0) return null;
        return $"{used.Value / (1024 * 1024)} MB / {total.Value / (1024 * 1024)} MB";
    }

    private static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private sealed class RadeonLoadSampler
    {
        private const uint ReadRegRequest = 0x24;
        private const uint VramUsageRequest = 0x1e;
        private const uint GttUsageRequest = 0x1f;
        private const uint GrbmStatus = 0x8010;
        private const uint GuiBit = 1u << 31;
        // _IOWR('d', 0x40 + 0x27, struct drm_radeon_info) with a 16-byte struct.
        private const uint IoctlRadeonInfo = 0xC0106467;
        // _IOWR('d', 0x40 + 0x1c, struct drm_radeon_gem_info) with a 24-byte struct.
        private const uint IoctlRadeonGemInfo = 0xC018645C;
        private const int O_RdWr = 2;
        private const int O_CloExec = 0x80000;
        private const int SampleMs = 20;
        private const int WindowMs = 1000;

        private readonly string _node;
        private readonly object _gate = new();
        private double? _percent;
        private string? _vramText;
        private string? _gttText;
        private bool _unavailable;
        private int _started;

        public RadeonLoadSampler(string node) => _node = node;

        public double? Percent
        {
            get { lock (_gate) return _percent; }
        }

        public (string? Vram, string? Gtt) Memory
        {
            get { lock (_gate) return (_vramText, _gttText); }
        }

        public bool Warming
        {
            get { lock (_gate) return _percent == null && !_unavailable; }
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            var thread = new Thread(Run) { IsBackground = true, Name = "radeon-gpu-load" };
            thread.Start();
        }

        private void Run()
        {
            try
            {
                int fd = open(_node, O_RdWr | O_CloExec);
                if (fd < 0) return;
                try
                {
                    IntPtr value = Marshal.AllocHGlobal(sizeof(ulong));
                    try
                    {
                        ReadMemory(fd, value);
                        int busy = 0, total = 0, failures = 0;
                        var window = Stopwatch.StartNew();
                        while (true)
                        {
                            if (!ReadRegister(fd, value, GrbmStatus, out uint status))
                            {
                                if (++failures >= 3) return;
                                Thread.Sleep(500);
                                continue;
                            }
                            failures = 0;
                            total++;
                            // shortcut: 50 Hz sampling of the GUI-busy bit over
                            // 1 s; radeontop uses 120 Hz. Fine for a usage
                            // graph, increase if short bursts matter.
                            if ((status & GuiBit) != 0) busy++;
                            if (window.ElapsedMilliseconds >= WindowMs)
                            {
                                double pct = total == 0 ? 0 : busy * 100.0 / total;
                                lock (_gate) _percent = Math.Round(pct, 1);
                                busy = 0;
                                total = 0;
                                window.Restart();
                                ReadMemory(fd, value);
                            }
                            Thread.Sleep(SampleMs);
                        }
                    }
                    finally { Marshal.FreeHGlobal(value); }
                }
                finally { close(fd); }
            }
            catch { }
            finally
            {
                lock (_gate)
                {
                    _percent = null;
                    _vramText = null;
                    _gttText = null;
                    _unavailable = true;
                }
            }
        }

        private void ReadMemory(int fd, IntPtr buffer)
        {
            var gem = new RadeonGemInfo();
            long? vramTotal = null, gttTotal = null;
            if (ioctlGem(fd, (UIntPtr)IoctlRadeonGemInfo, ref gem) == 0)
            {
                vramTotal = (long)gem.VramSize;
                gttTotal = (long)gem.GartSize;
            }

            string? vram = FormatBytePair(ReadUsage(fd, buffer, VramUsageRequest), vramTotal);
            string? gtt = FormatBytePair(ReadUsage(fd, buffer, GttUsageRequest), gttTotal);
            lock (_gate)
            {
                _vramText = vram;
                _gttText = gtt;
            }
        }

        private static long? ReadUsage(int fd, IntPtr buffer, uint request)
        {
            var info = new RadeonInfo { Request = request, Value = (ulong)buffer };
            if (ioctl(fd, (UIntPtr)IoctlRadeonInfo, ref info) != 0) return null;
            return Marshal.ReadInt64(buffer);
        }

        private static bool ReadRegister(int fd, IntPtr buffer, uint register, out uint value)
        {
            value = 0;
            Marshal.WriteInt32(buffer, 0, unchecked((int)register));
            var info = new RadeonInfo { Request = ReadRegRequest, Value = (ulong)buffer };
            if (ioctl(fd, (UIntPtr)IoctlRadeonInfo, ref info) != 0) return false;
            value = unchecked((uint)Marshal.ReadInt32(buffer));
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RadeonInfo
        {
            public uint Request;
            public uint Pad;
            public ulong Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RadeonGemInfo
        {
            public ulong GartSize;
            public ulong VramSize;
            public ulong VramVisible;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, UIntPtr request, ref RadeonInfo arg);

        [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
        private static extern int ioctlGem(int fd, UIntPtr request, ref RadeonGemInfo arg);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
    }
}
