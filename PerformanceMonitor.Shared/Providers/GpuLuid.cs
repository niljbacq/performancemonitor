using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

// Windows' GPU perf counters are named "pid_.._luid_0xHHHHHHHH_0xLLLLLLLL_phys_N_eng_M_engtype_X".
// The LUID identifies the adapter; phys_N is only an index inside one adapter, so it
// is NOT a position in the Win32_VideoController list. On an iGPU + dGPU laptop the
// two orders differ, which made one card show the other's utilization (or nothing).
// This maps each LUID to the adapter's display name through D3DKMT so the counters can
// be matched to the right GpuInfo by name.
[SupportedOSPlatform("windows")]
public static class GpuLuid
{
    private const uint AdapterRegistryInfo = 8; // KMTQAITYPE_ADAPTERREGISTRYINFO
    private const int MaxPath = 260;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 adapters);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    /// <summary>LUID key ("0x00000000_0x0000d3a5") from a counter instance name, or null.</summary>
    public static string? FromInstance(string instance)
    {
        int idx = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int start = idx + 5;
        int end = instance.IndexOf("_phys_", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return instance[start..end].ToLowerInvariant();
    }

    /// <summary>LUID key -> adapter description for every WDDM adapter (empty on failure).</summary>
    public static Dictionary<string, string> ReadAdapterNames()
    {
        var names = new Dictionary<string, string>();
        try
        {
            var list = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref list) != 0) return names;
            int count = (int)list.NumAdapters;
            if (count <= 0) return names;

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            IntPtr buffer = Marshal.AllocHGlobal(size * count);
            try
            {
                list.NumAdapters = (uint)count;
                list.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref list) != 0) return names;

                count = (int)list.NumAdapters;
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(IntPtr.Add(buffer, i * size));
                    // Unlike GpuTemperature, keep adapters with no display sources:
                    // the dGPU of a hybrid laptop usually has none.
                    string? name = ReadName(info.AdapterLuid);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string key = $"0x{(uint)info.AdapterLuid.HighPart:x8}_0x{info.AdapterLuid.LowPart:x8}";
                    names[key] = name;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Unresolved adapters fall back to positional matching.
        }
        return names;
    }

    /// <summary>
    /// GpuInfo list index -> LUID key, matched by adapter name. Several identical
    /// cards are paired in LUID order. Adapters that cannot be matched are absent.
    /// </summary>
    public static Dictionary<int, string> MatchByName(IReadOnlyList<GpuInfo> gpus, Dictionary<string, string> luidNames)
    {
        var result = new Dictionary<int, string>();
        if (luidNames.Count == 0) return result;

        var taken = new HashSet<string>();
        for (int i = 0; i < gpus.Count; i++)
        {
            string key = Normalize(gpus[i].Name);
            if (key.Length == 0) continue;

            var candidates = luidNames
                .Where(kv => !taken.Contains(kv.Key) && Normalize(kv.Value) == key)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
            {
                candidates = luidNames
                    .Where(kv =>
                    {
                        if (taken.Contains(kv.Key)) return false;
                        string other = Normalize(kv.Value);
                        return other.Length > 0 && (other.Contains(key) || key.Contains(other));
                    })
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToList();
            }
            if (candidates.Count == 0) continue;

            result[i] = candidates[0].Key;
            taken.Add(candidates[0].Key);
        }
        return result;
    }

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = name.ToLowerInvariant().Replace("(tm)", "").Replace("(r)", "");
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    private static string? ReadName(Luid luid)
    {
        var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;

        IntPtr data = IntPtr.Zero;
        try
        {
            // D3DKMT_ADAPTERREGISTRYINFO: four WCHAR[MAX_PATH] strings; the first is the adapter description.
            int size = 4 * MaxPath * 2;
            data = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(data, i, 0);

            var query = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = open.hAdapter,
                Type = AdapterRegistryInfo,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)size
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
            return Marshal.PtrToStringUni(data);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public Luid AdapterLuid;
        public uint NumOfSources;
        public int PreferAdapterOverDisplay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public Luid AdapterLuid;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public uint Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }
}
