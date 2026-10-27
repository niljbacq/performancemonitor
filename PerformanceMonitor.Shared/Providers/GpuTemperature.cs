using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

// The WDDM kernel driver answers a temperature query through D3DKMT for any
// vendor. Drivers that do not implement it fail the query or return an
// implausible value; both return null so the row hides instead of showing 0 °C.
[SupportedOSPlatform("windows")]
public static class GpuTemperature
{
    private const uint AdapterPerfData = 62; // KMTQAITYPE_ADAPTERPERFDATA

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 adapters);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    /// <summary>
    /// One entry per adapter that drives a display, in enumeration order;
    /// null = no reading. Render-only adapters are skipped: mixing them in
    /// would shift the WMI adapter list against these entries.
    /// </summary>
    public static IReadOnlyList<double?> ReadAll()
    {
        var temps = new List<double?>();
        try
        {
            var list = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref list) != 0) return temps;
            int count = (int)list.NumAdapters;
            if (count <= 0) return temps;

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            IntPtr buffer = Marshal.AllocHGlobal(size * count);
            try
            {
                list.NumAdapters = (uint)count;
                list.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref list) != 0) return temps;

                count = (int)list.NumAdapters;
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(IntPtr.Add(buffer, i * size));
                    if (info.NumOfSources == 0) continue;
                    temps.Add(ReadOne(info.AdapterLuid));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return temps;
        }
        return temps;
    }

    private static double? ReadOne(Luid luid)
    {
        var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;

        IntPtr data = IntPtr.Zero;
        try
        {
            int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
            data = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA(), data, false);

            var query = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = open.hAdapter,
                Type = AdapterPerfData,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)size
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;

            var perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(data);
            // Tenths of a degree; anything outside 1..150 °C is a driver that
            // answered with a placeholder, not a temperature.
            return perf.Temperature is < 10 or > 1500 ? null : perf.Temperature / 10.0;
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

    // d3dkmthk.h D3DKMT_ADAPTER_PERFDATA; Temperature is in tenths of °C.
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }
}

// Mirrors the Linux reason row when an adapter exposes no utilization counter.
public static class GpuReason
{
    public static string ForUtilization(string? driverName) =>
        string.IsNullOrWhiteSpace(driverName)
            ? "Utilization not exposed by this GPU driver"
            : $"Utilization not exposed by the {driverName} driver";
}
