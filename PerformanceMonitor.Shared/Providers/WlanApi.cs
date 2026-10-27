using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManager.Providers;

// Formatting shared by the WlanAPI path and the netsh fallback.
public static class WifiText
{
    public static int? PercentFromRssi(int dbm) => Math.Clamp(2 * (dbm + 100), 0, 100);

    public static string StandardFromPhy(string? phy) => phy switch
    {
        "802.11be" => "Wi-Fi 7 (802.11be)",
        "802.11ax" => "Wi-Fi 6 (802.11ax)",
        "802.11ac" => "Wi-Fi 5 (802.11ac)",
        "802.11n" => "Wi-Fi 4 (802.11n)",
        _ => "802.11a/b/g"
    };
}

internal readonly record struct WlanState(
    string AdapterName, bool Connected, string? Ssid, string? Bssid, int? RssiDbm, string? Phy, string? Band);

// WlanAPI replaces the netsh child process. Every failure returns null or an
// unconnected state so the caller can fall back to netsh instead of showing
// a value nobody measured.
internal static class WlanApi
{
    private const uint ClientVersion = 2;
    private const uint OpcodeCurrentConnection = 7;
    private const uint StateConnected = 1;

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint opCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
        out uint pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid, uint dot11BssType, bool bSecurityEnabled, IntPtr pReserved,
        out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    public static WlanState? Query()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle) != 0) return null;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) return null;

            int count = Marshal.ReadInt32(list);
            if (count <= 0) return null;

            // Prefer an associated adapter; a disabled or idle one has no BSS.
            int infoSize = Marshal.SizeOf<WlanInterfaceInfo>();
            Guid guid = Guid.Empty;
            string name = "";
            bool haveConnected = false;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(list, 8 + i * infoSize));
                bool connected = info.State == StateConnected;
                if (i == 0 || (connected && !haveConnected))
                {
                    guid = info.InterfaceGuid;
                    name = info.Description ?? "";
                    haveConnected = connected;
                }
            }

            if (WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero,
                    out _, out IntPtr data, out _) != 0 || data == IntPtr.Zero)
                return null; // query failed: let the caller fall back to netsh
            try
            {
                var attrs = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                if (attrs.State != StateConnected)
                    return new WlanState(name, false, null, null, null, null, null);

                string? ssid = DecodeSsid(attrs.Association.Ssid);
                string? bssid = MacToString(attrs.Association.Bssid);
                string? phy = PhyText(attrs.Association.PhyType);
                var (rssi, band) = ReadBss(handle, guid, attrs.Association.Bssid);
                return new WlanState(name, true, ssid, bssid, rssi, phy, band);
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    // RSSI and centre frequency live on the BSS entry, not on the association.
    private static (int? Rssi, string? Band) ReadBss(IntPtr handle, Guid guid, byte[] bssid)
    {
        IntPtr bssList = IntPtr.Zero;
        try
        {
            if (WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 1 /* any */, false,
                    IntPtr.Zero, out bssList) != 0 || bssList == IntPtr.Zero)
                return (null, null);

            int count = Marshal.ReadInt32(bssList, 4);
            int entrySize = Marshal.SizeOf<WlanBssEntry>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WlanBssEntry>(IntPtr.Add(bssList, 8 + i * entrySize));
                if (!SameMac(entry.Bssid, bssid)) continue;
                return (entry.Rssi, BandText(entry.CenterFrequency));
            }
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (bssList != IntPtr.Zero) WlanFreeMemory(bssList);
        }
    }

    private static string? BandText(uint centerFrequencyKhz) => centerFrequencyKhz switch
    {
        >= 5925_000 => "6 GHz",
        >= 5000_000 => "5 GHz",
        >= 2412_000 and <= 2484_000 => "2.4 GHz",
        _ => null
    };

    // DOT11_PHY_TYPE: 7 = ht (11n), 8 = vht (11ac), 10 = he (11ax), 11 = eht (11be).
    private static string? PhyText(uint phyType) => phyType switch
    {
        11 => "802.11be",
        10 => "802.11ax",
        8 => "802.11ac",
        7 => "802.11n",
        _ => null
    };

    private static string? DecodeSsid(Dot11Ssid ssid)
    {
        if (ssid.Ssid is null || ssid.Length == 0) return null;
        int length = Math.Min((int)ssid.Length, ssid.Ssid.Length);
        return Encoding.UTF8.GetString(ssid.Ssid, 0, length);
    }

    private static string? MacToString(byte[]? mac) =>
        mac is { Length: 6 } ? string.Join(":", Array.ConvertAll(mac, b => b.ToString("X2"))) : null;

    private static bool SameMac(byte[]? a, byte[]? b) =>
        a is { Length: 6 } && b is { Length: 6 } && a.AsSpan().SequenceEqual(b);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public uint BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;
        public uint PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        public int SecurityEnabled;
        public int OneXEnabled;
        public uint AuthAlgorithm;
        public uint CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public uint State;
        public uint ConnectionMode;
        // Inline WCHAR[256] in the SDK, not a pointer: it is what puts the
        // association at offset 520 (struct size 604).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;
        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanRateSet
    {
        public uint RateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] Rates;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanBssEntry
    {
        public Dot11Ssid Ssid;                        // 0
        public uint PhyId;                            // 36
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;                          // 40
        public uint BssType;                          // 48
        public uint PhyType;                          // 52
        public int Rssi;                              // 56
        public uint LinkQuality;                      // 60
        public byte InRegDomain;                      // 64
        public ushort BeaconPeriod;                   // 66
        public ulong Timestamp;                       // 72
        public ulong HostTimestamp;                   // 80
        public ushort CapabilityInformation;          // 88
        public uint CenterFrequency;                  // 92
        public WlanRateSet RateSet;                   // 96
        public uint IeOffset;                         // 352
        public uint IeSize;                           // 356, struct 360
    }
}
