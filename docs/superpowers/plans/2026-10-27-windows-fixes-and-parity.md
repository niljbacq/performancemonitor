# Windows Fixes and Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the three Windows defects (console window, inverted SSD/HDD, missing Start menu entry) and fill every Windows detail row that Windows can honestly answer, matching Linux where the OS provides the same data.

**Architecture:** All changes are inside the existing Windows providers plus one GUI flag; the shared UI and the Linux/Android providers stay untouched. Wi-Fi details come from `netsh wlan show interfaces` plus `NetworkInterface`; live CPU clock comes from the `ProcessorInformation` performance counter. Rows with no Windows source stay hidden.

**Tech Stack:** C#, .NET 8 (`net8.0`), Avalonia 12, `System.Management`, `System.Net.NetworkInformation`, Win32 P/Invoke (`DeviceIoControl`, `SHChangeNotify`).

**Spec:** `docs/superpowers/specs/2026-10-27-windows-fixes-design.md`

## Global Constraints

- The repo is C# only. No shell, Python, or PowerShell source files are added to the repo; documentation stays Markdown. Ad-hoc commands below are verification only.
- No new NuGet package, no kernel driver, no administrator requirement.
- Do not change Linux or Android providers, and do not change `MainView.axaml` or any shared XAML.
- Target framework stays `net8.0`; `RollForward` stays `LatestMajor`; publish flags in `.github/workflows/build.yml` stay as they are.
- When Windows cannot answer a row, the provider returns `null` for that field and the row hides. Never substitute a different metric.
- The published Windows executable must use the GUI subsystem (no console window).

## Review Focus

- No wireless adapter, or `wlansvc` stopped: `GetSnapshot()` must return a disconnected snapshot, never throw. (Task 4, no-adapter check on this machine.)
- A moved or re-downloaded exe leaves a stale Start menu target: launching the new copy must rewrite the shortcut. (Task 3, copy-and-run check.)
- The IOCTL returns an older/short seek-penalty descriptor: the classifier must fall back to WMI instead of guessing. (Task 2, size guard plus real-disk check.)
- The performance counter is absent, zero, or localized: CPU speed must fall back to the max clock and keep updating. (Task 5, fallback shown in code and checked by the scratch app.)
- The wireless adapter is disabled while another is up: the snapshot must prefer the Up adapter and still parse values, not labels. (Task 4, provider logic plus a real Wi-Fi machine check.)

---

### Task 1: GUI subsystem (no console window)

**Files:**
- Modify: `PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj:3`

**Interfaces:**
- Consumes: nothing.
- Produces: published exe with PE subsystem `2` (`IMAGE_SUBSYSTEM_WINDOWS_GUI`).

- [ ] **Step 1: Change the output type**

Replace `<OutputType>Exe</OutputType>` with `<OutputType>WinExe</OutputType>`.

- [ ] **Step 2: Build the desktop project**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 3: Publish win-x64 with the release flags**

Run:
```
dotnet publish "PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o C:\Users\nj\AppData\Local\Temp\opencode\winpublish
```
Expected: `PerformanceMonitor.Desktop.exe` exists in that folder.

- [ ] **Step 4: Verify the PE subsystem**

Run:
```powershell
$b=[IO.File]::ReadAllBytes("C:\Users\nj\AppData\Local\Temp\opencode\winpublish\PerformanceMonitor.Desktop.exe"); $pe=[BitConverter]::ToInt32($b,0x3C); "{0}" -f [BitConverter]::ToUInt16($b,$pe+0x5C)
```
Expected: `2`.

- [ ] **Step 5: Commit**

```bash
git add PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj
git commit -m "fix(windows): build as WinExe so no console window opens"
```

---

### Task 2: Correct SSD/HDD classification

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/DiskProviders.cs:339-344`
- Test (outside repo): `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\` (console app referencing `PerformanceMonitor.Shared`)

**Interfaces:**
- Consumes: `WindowsDiskProvider.GetDiskList(): IReadOnlyList<DiskInfo>` (existing, unchanged).
- Produces: `SeekPenaltyClassifier.IsSsd(int physicalDriveIndex): bool?` (existing signature; `true` = SSD, `null` = unknown, so `BuildDiskList` falls back to WMI).

- [ ] **Step 1: Create the scratch checker**

```powershell
$d="$env:TEMP\opencode\paritycheck"; New-Item -ItemType Directory -Force $d | Out-Null; Set-Location $d; dotnet new console --force | Out-Null
```
Write `paritycheck.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RollForward>LatestMajor</RollForward>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="A:\Documents\performance monitor\PerformanceMonitor.Shared\PerformanceMonitor.Shared.csproj" />
  </ItemGroup>
</Project>
```
Write `Program.cs`:
```csharp
using TaskManager.Providers;

foreach (var d in new WindowsDiskProvider().GetDiskList())
    Console.WriteLine($"{d.ModelName} | {d.Type} | {d.Capacity} | {d.DeviceId}");
```

- [ ] **Step 2: Run it and confirm the bug**

Run: `dotnet run -c Release` in the scratch folder.
Expected before the fix: Toshiba and Samsung both print `HDD`.

- [ ] **Step 3: Read the descriptor the documented way**

In `SeekPenaltyClassifier.IsSsd`, replace the single `ReadByte(outBuffer, 4)` block with:

```csharp
// DEVICE_SEEK_PENALTY_DESCRIPTOR: Version (4), Size (4), IncursSeekPenalty (1) at offset 8.
int descriptorSize = System.Runtime.InteropServices.Marshal.ReadInt32(outBuffer, 4);
if (descriptorSize < 9) return null;
bool incursSeekPenalty = System.Runtime.InteropServices.Marshal.ReadByte(outBuffer, 8) != 0;
return !incursSeekPenalty; // False penalty = SSD, True penalty = HDD
```

- [ ] **Step 4: Re-run the checker**

Run: `dotnet run -c Release` in the scratch folder.
Expected: `TOSHIBA DT01ACA100 | HDD` and `Samsung SSD 850 PRO 128GB | SSD`.

- [ ] **Step 5: Build the desktop project**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `Build succeeded`.

- [ ] **Step 6: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/DiskProviders.cs
git commit -m "fix(windows): read seek-penalty flag at its documented offset"
```

---

### Task 3: Refresh the Start menu shortcut and notify the shell

**Files:**
- Modify: `PerformanceMonitor.Desktop/ShortcutHelper.cs`

**Interfaces:**
- Consumes: `ShortcutHelper.TryCreateStartMenuShortcut()` (existing, called from `Program.Main`).
- Produces: same signature; the shortcut target is rewritten when it differs from the running exe, and the shell is notified on every launch.

- [ ] **Step 1: Add the shell notification call**

Add `using System.Runtime.InteropServices;` and inside the class:

```csharp
[DllImport("shell32.dll")]
private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

private const int SHCNE_ASSOCCHANGED = 0x08000000;
private const uint SHCNF_IDLIST = 0x0000;
```

- [ ] **Step 2: Rewrite a stale target instead of skipping it**

Replace:

```csharp
if (File.Exists(lnkPath)) return; // Idempotent: skip if user already has it
```

with logic that reads the existing target first:

```csharp
var shellType = Type.GetTypeFromProgID("WScript.Shell");
if (shellType is null) return;

dynamic shell = Activator.CreateInstance(shellType)!;
dynamic shortcut = shell.CreateShortcut(lnkPath);

bool targetMatches = File.Exists(lnkPath) &&
    string.Equals((string?)shortcut.TargetPath, exe, StringComparison.OrdinalIgnoreCase);

if (!targetMatches)
{
    shortcut.TargetPath = exe;
    shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
    shortcut.Description = "Performance Monitor";

    string iconPath = Path.Combine(Path.GetDirectoryName(exe)!, "Assets", "app.ico");
    shortcut.IconLocation = File.Exists(iconPath) ? iconPath : $"{exe},0";
    shortcut.Save();
}

// Windows caches Start menu apps; ask it to re-read them on every run.
SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
```

Remove the now-duplicated `shellType`/`shell`/`shortcut` declarations that followed the old early return.

- [ ] **Step 3: Build**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `Build succeeded`.

- [ ] **Step 4: Prove the shortcut self-heals**

Copy the published folder from Task 1 to `C:\Users\nj\AppData\Local\Temp\opencode\winpublish2`, run `PerformanceMonitor.Desktop.exe` from there, then read the shortcut target:

```powershell
$s=New-Object -ComObject WScript.Shell; $s.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Performance Monitor.lnk").TargetPath
```
Expected: the path inside `winpublish2`. Then run the original `winpublish\PerformanceMonitor.Desktop.exe` once so the shortcut points back at the real release copy.

- [ ] **Step 5: Commit**

```bash
git add PerformanceMonitor.Desktop/ShortcutHelper.cs
git commit -m "fix(windows): refresh stale Start menu shortcut and notify the shell"
```

---

### Task 4: Windows Wi-Fi snapshot

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/WifiProviders.cs:12-55`

**Interfaces:**
- Consumes: `IWifiProvider.GetSnapshot(): WifiSnapshot?` (default returns `null`), `CommandRunner.Run(string, string, int)`.
- Produces: `WindowsWifiProvider.GetSnapshot()` filling every field of `WifiSnapshot` that Windows can answer. Fields with no data stay `null`; disconnected returns a snapshot with `Status = "Disconnected"`.

- [ ] **Step 1: Extract the netsh pair parser**

Add a private helper used by both `GetDetails` and `GetSnapshot`:

```csharp
private static List<(string Label, string Value)> ReadNetshPairs()
{
    string output = CommandRunner.Run("netsh", "wlan show interfaces", 2000);
    var pairs = new List<(string Label, string Value)>();
    foreach (string line in output.Split('\n'))
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) continue;
        pairs.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
    }
    return pairs;
}
```
Make `GetDetails` call it instead of its inline copy.

- [ ] **Step 2: Implement `GetSnapshot`**

Add to `WindowsWifiProvider`, following the Linux field mapping exactly:

```csharp
public WifiSnapshot? GetSnapshot()
{
    var nic = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
        .FirstOrDefault();

    string adapter = string.IsNullOrWhiteSpace(nic?.Description) ? "Wi-Fi" : nic!.Description;
    if (nic == null)
        return new WifiSnapshot(adapter, "Disconnected", "No wireless adapter", "-", "-", "-", "-", null, null);

    var pairs = ReadNetshPairs();

    // Labels are localized; values are not. Identify each field by value shape.
    int bssidIndex = pairs.FindIndex(p => Regex.IsMatch(p.Value, @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"));
    string? bssid = bssidIndex >= 0 ? pairs[bssidIndex].Value : null;
    string? ssid = bssidIndex > 0 ? pairs[bssidIndex - 1].Value : null;
    string? signalText = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^\d{1,3}\s*%$")).Value;
    string? radio = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^802\.11")).Value;
    string? band = pairs.FirstOrDefault(p => Regex.IsMatch(p.Value, @"^(2\.4|5|6)\s*GHz$")).Value;

    if (nic.OperationalStatus != OperationalStatus.Up || bssid == null)
        return new WifiSnapshot(adapter, "Disconnected", "Not connected", "-", "-", "-", "-", null, null);

    string standard = radio switch
    {
        "802.11be" => "Wi-Fi 7 (802.11be)",
        "802.11ax" => "Wi-Fi 6 (802.11ax)",
        "802.11ac" => "Wi-Fi 5 (802.11ac)",
        "802.11n" => "Wi-Fi 4 (802.11n)",
        _ => "802.11a/b/g"
    };
    int? percent = int.TryParse(signalText?.TrimEnd('%', ' '), out int p) ? Math.Clamp(p, 0, 100) : null;

    string ipv4 = "-", ipv6 = "-", mask = "-";
    string? gateway = null, dhcp = null;
    var dns = new List<string>();
    long? sent = null, received = null;
    try
    {
        var ip = nic.GetIPProperties();
        foreach (var ua in ip.UnicastAddresses)
        {
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ipv4 == "-")
            {
                ipv4 = ua.Address.ToString();
                mask = ua.IPv4Mask?.ToString() ?? "-";
            }
            else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6 == "-"
                     && !ua.Address.IsIPv6LinkLocal)
            {
                ipv6 = ua.Address.ToString();
            }
        }
        gateway = ip.GatewayAddresses.FirstOrDefault(g => g.Address.ToString() != "0.0.0.0")?.Address.ToString();
        dhcp = ip.DhcpServerAddresses.FirstOrDefault()?.ToString();
        dns = ip.DnsAddresses.Select(a => a.ToString()).Take(2).ToList();

        var stats = nic.GetIPv4Statistics();
        sent = stats.BytesSent;
        received = stats.BytesReceived;
    }
    catch { }

    return new WifiSnapshot(adapter, "Connected", ssid ?? "Connected",
        string.Join(" ", new[] { "802.11", standard, band }.Where(s => !string.IsNullOrEmpty(s))),
        percent is int sp ? $"📶 {sp}%" : "-", ipv4, ipv6, sent, received)
    {
        WifiStandard = standard,
        SignalPercentText = percent is int q ? $"{q}%" : null,
        Bssid = bssid,
        Gateway = gateway,
        Dns1 = dns.ElementAtOrDefault(0),
        Dns2 = dns.ElementAtOrDefault(1),
        Netmask = mask,
        DhcpServer = dhcp,
        DownloadText = received is long rx ? $"{rx / 1048576.0:F1} MB since boot" : null,
        UploadText = sent is long tx ? $"{tx / 1048576.0:F1} MB since boot" : null
    };
}
```

- [ ] **Step 3: Build**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `Build succeeded`.

- [ ] **Step 4: Check the no-adapter path on this machine**

Add to the scratch `Program.cs`:

```csharp
var snap = new WindowsWifiProvider().GetSnapshot();
Console.WriteLine(snap is null
    ? "snapshot: null"
    : $"{snap.AdapterName} | {snap.Status} | {snap.Ssid} | {snap.ConnectionType} | {snap.SignalStrength}");
```

Run: `dotnet run -c Release` in the scratch folder.
Expected here (Ethernet only): `Wi-Fi | Disconnected | No wireless adapter | - | -`, no exception.

- [ ] **Step 5: Real Wi-Fi check (manual, user)**

On a machine with a wireless adapter, open the Wi-Fi tab and confirm SSID, signal %, BSSID, gateway, DHCP, DNS 1/2, netmask, and totals since boot match `netsh wlan show interfaces` and `ipconfig /all`.

- [ ] **Step 6: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/WifiProviders.cs
git commit -m "feat(windows): fill Wi-Fi detail rows on Windows"
```

---

### Task 5: Live CPU clock on Windows

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs:92-105`
- Modify: `PerformanceMonitor.Shared/Providers/CpuProviders.cs:108-156`
- Modify: `PerformanceMonitor.Shared/ViewModels/CpuViewModel.cs:180-195`

**Interfaces:**
- Consumes: `ICpuProvider` (default members pattern already used for `PerCoreFrequencyText`).
- Produces: `ICpuProvider.CurrentSpeedText => null` (default); `WindowsCpuProvider` overrides it with a throttled live clock string such as `"3.90 GHz"`; `CpuViewModel` refreshes `Speed` while monitoring.

- [ ] **Step 1: Add the default interface member**

In `ICpuProvider`, next to `PerCoreFrequencyText`:

```csharp
string? CurrentSpeedText => null;
```

- [ ] **Step 2: Implement the Windows live clock**

In `WindowsCpuProvider`, add fields and the property:

```csharp
private DateTime _speedSampleAt;
private string? _speedText;

public string? CurrentSpeedText
{
    get
    {
        if (DateTime.UtcNow - _speedSampleAt < TimeSpan.FromSeconds(2)) return _speedText;
        _speedSampleAt = DateTime.UtcNow;
        try
        {
            // Average the per-core rows; _Total is not a clock.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation");
            double sum = 0;
            int count = 0;
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.EndsWith("_Total", StringComparison.Ordinal) ||
                    name.EndsWith("_Average", StringComparison.Ordinal)) continue;
                if (!double.TryParse(obj["ProcessorFrequency"]?.ToString(), out double mhz) || mhz <= 0) continue;
                sum += mhz;
                count++;
            }
            if (count > 0) _speedText = $"{Math.Round(sum / count / 1000.0, 2):F2} GHz";
        }
        catch { }
        return _speedText;
    }
}
```

In `GetSpecs`, after `s.BaseSpeed` is set, change `s.Speed = s.BaseSpeed;` to `s.Speed = CurrentSpeedText ?? s.BaseSpeed;`.

- [ ] **Step 3: Refresh the row while monitoring**

In `CpuViewModel.StartMonitoringAsync`, after `string? appUsageText = _provider.AppUsageText;`:

```csharp
string? speedText = _provider.CurrentSpeedText;
```

and inside the `Dispatcher.UIThread.Post` block, after the `AppUsageText` line:

```csharp
if (speedText != null) Speed = speedText;
```

- [ ] **Step 4: Build**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `Build succeeded`.

- [ ] **Step 5: Check that the value moves**

Add to the scratch `Program.cs`:

```csharp
var cpu = new WindowsCpuProvider();
Console.WriteLine($"specs speed: {cpu.GetSpecs().Speed}");
var load = new Thread(() =>
{
    var end = DateTime.UtcNow.AddSeconds(8);
    double x = 1;
    while (DateTime.UtcNow < end) { for (int i = 0; i < 1_000_000; i++) x = x * 1.0000001 % 1000; }
});
load.Start();
for (int i = 0; i < 5; i++)
{
    Thread.Sleep(2000);
    Console.WriteLine($"live: {cpu.CurrentSpeedText}");
}
```

Run: `dotnet run -c Release` in the scratch folder.

Expected: `specs speed` is a GHz value, and at least one `live:` sample differs from it (on this machine idle reads ~3.90 GHz, load reads ~1.70 GHz). If the counter is missing, `specs speed` equals the max clock and `live:` repeats or stays empty — that is the fallback, not a failure.

- [ ] **Step 6: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/Abstractions.cs PerformanceMonitor.Shared/Providers/CpuProviders.cs PerformanceMonitor.Shared/ViewModels/CpuViewModel.cs
git commit -m "feat(windows): show the live CPU clock"
```

---

### Task 6: End-to-end verification

**Files:**
- No source changes. Verification and documentation check only.

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces: a release build that opens without a console, classifies both disks correctly, rewrites its shortcut, and shows live Wi-Fi/CPU values; Linux build still compiles.

- [ ] **Step 1: Publish both desktop targets**

Run the Task 1 publish command for `win-x64`.
Then run:
```
dotnet publish "PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o C:\Users\nj\AppData\Local\Temp\opencode\linuxpublish
```
Expected: both succeed; the Linux one proves the shared changes did not break the Linux build.

- [ ] **Step 2: Full shared build**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: 0 errors.

- [ ] **Step 3: Launch the published Windows exe**

Run `C:\Users\nj\AppData\Local\Temp\opencode\winpublish\PerformanceMonitor.Desktop.exe`.
Expected: window opens with no console window; Disk tab shows Toshiba under HDD and Samsung under SSD; CPU shows a Speed value; Wi-Fi tab shows the no-adapter state on this machine.

- [ ] **Step 4: Confirm no tracked leftovers**

Run: `git status --short`
Expected: only the files from Tasks 1-5 plus the spec and this plan. No `publish/`, `bin/`, `obj/`, or scratch files.

- [ ] **Step 5: Commit the documentation**

```bash
git add docs/superpowers/specs/2026-10-27-windows-fixes-design.md docs/superpowers/plans/2026-10-27-windows-fixes-and-parity.md
git commit -m "docs(windows): record fixes and parity scope"
```
