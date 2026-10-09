using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

[SupportedOSPlatform("windows")]
public sealed class WindowsGpuProvider : IGpuProvider, IGpuListProvider
{
    // GPU Engine 3D counters, grouped per phys_N adapter ordinal, primed once.
    private readonly Dictionary<int, List<PerformanceCounter>> _perfCounters = new();
    private bool _perfCountersBuilt;
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
        // summed per phys_N adapter and assigned in phys order (best effort;
        // Windows does not expose a reliable WMI-to-phys mapping).
        var byPhys = SamplePerfByPhys();
        int physIdx = 0;
        foreach (var g in result)
        {
            if (g.Utilization != null) continue;
            if (physIdx >= byPhys.Count) break;
            g.Utilization = byPhys[physIdx];
            physIdx++;
        }

        return result;
    }

    private List<GpuInfo> BuildList()
    {
        var list = new List<GpuInfo>();
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
                    DriverVersion = obj["DriverVersion"]?.ToString() ?? "N/A",
                    GraphicsApi = "DirectX 12",
                    PhysicalLocation = pnp ?? "PCI Bus",
                    Vendor = VendorFromName(name),
                    Kind = GpuClassifier.Classify(name, pnp)
                };
                if (DateTime.TryParse(obj["DriverDate"]?.ToString(), out var date))
                    info.DriverDate = date.ToShortDateString();
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
                GraphicsApi = "DirectX 12",
                Kind = "Unknown"
            });
        }

        // Enrich NVIDIA specs (name, driver, PCI bus) without live values.
        NvidiaSmi.Apply(list, NvidiaSmi.Query(), includeLive: false);
        return list;
    }

    private IReadOnlyList<float> SamplePerfByPhys()
    {
        EnsurePerfCounters();
        var byPhys = new SortedDictionary<int, float>();
        foreach (var kv in _perfCounters)
        {
            float total = 0;
            foreach (var counter in kv.Value)
                total += counter.NextValue();
            byPhys[kv.Key] = (float)Math.Min(Math.Round(total, 1), 100);
        }
        return byPhys.Values.ToList();
    }

    private void EnsurePerfCounters()
    {
        if (_perfCountersBuilt) return;
        _perfCountersBuilt = true;
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                if (!instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                int? phys = PhysIndex(instance);
                if (phys == null) continue;
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                counter.NextValue(); // prime the rate baseline
                if (!_perfCounters.TryGetValue(phys.Value, out var list))
                    _perfCounters[phys.Value] = list = new List<PerformanceCounter>();
                list.Add(counter);
            }
        }
        catch { }
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
            return "Unknown";
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
            if (row.Utilization is double u) g.Utilization = u;
            if (row.TemperatureC is double t) g.TemperatureC = t;
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

    public static GpuInfo? Primary(IReadOnlyList<GpuInfo> list)
    {
        foreach (var g in list)
        {
            if (g.Kind == "Dedicated") return g;
        }
        return list.Count > 0 ? list[0] : null;
    }
}

public sealed class LinuxGpuProvider : IGpuProvider, IGpuListProvider
{
    private const string DrmRoot = "/sys/class/drm";
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
            try { FillSysfsSample(info); } catch { }
            result.Add(info);
        }
        NvidiaSmi.Apply(result, rows, includeLive: true);
        return result;
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

                found.Add((index, new GpuInfo
                {
                    DeviceId = card,
                    PciBus = slot,
                    Name = name,
                    Kind = GpuClassifier.Classify(name, vendorRaw),
                    Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor,
                    GraphicsApi = "Vulkan / OpenGL",
                    PhysicalLocation = slot ?? card
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
                    GraphicsApi = "Vulkan / OpenGL",
                    PhysicalLocation = entry.Slot
                });
            }
        }

        NvidiaSmi.Apply(list, NvidiaSmi.Query(), includeLive: false);
        return list;
    }

    private static void FillSysfsSample(GpuInfo info)
    {
        if (string.IsNullOrEmpty(info.DeviceId)) return;
        string device = Path.Combine(DrmRoot, info.DeviceId, "device");

        string busyPath = Path.Combine(device, "gpu_busy_percent");
        if (TryReadDouble(busyPath, out double busy))
            info.Utilization = busy;
        else
            TryDebugfsLoad(info);

        if (TryReadTempC(device, out double temp))
            info.TemperatureC = temp;

        string? vram = FormatBytePair(
            ReadLong(Path.Combine(device, "mem_info_vram_used")),
            ReadLong(Path.Combine(device, "mem_info_vram_total")));
        if (vram != null) info.MemoryUsage = vram;

        string? gtt = FormatBytePair(
            ReadLong(Path.Combine(device, "mem_info_gtt_used")),
            ReadLong(Path.Combine(device, "mem_info_gtt_total")));
        if (gtt != null) info.SharedMemoryUsage = gtt;
    }

    private static void TryDebugfsLoad(GpuInfo info)
    {
        if (info.DeviceId == null || !int.TryParse(info.DeviceId.AsSpan(4), out int index)) return;
        string path = $"/sys/kernel/debug/dri/{index}/amdgpu_pm_info";
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.Contains("GPU Load", StringComparison.OrdinalIgnoreCase)) continue;
            var tokens = line.Split(new[] { ':', '%', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && v >= 0 && v <= 100)
                {
                    info.Utilization = v;
                    return;
                }
            }
        }
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
        if (idx >= 0) return line[(idx + marker.Length)..].Trim();
        int colon = line.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < line.Length) return line[(colon + 1)..].Trim();
        return null;
    }

    private static bool IsCardName(string name, out int index)
    {
        index = -1;
        if (!name.StartsWith("card", StringComparison.Ordinal)) return false;
        return int.TryParse(name.AsSpan(4), out index);
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
            string full = Path.GetFullPath(devicePath);
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
}
