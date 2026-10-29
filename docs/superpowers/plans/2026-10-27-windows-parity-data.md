# Windows Data Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the Windows build to Linux parity in the data it shows, using Linux as the reference, and guarantee the Windows app never opens a console window or depends on a CLI tool where an API exists.

**Architecture:** All changes stay in the existing per-OS provider layer (`PerformanceMonitor.Shared/Providers`). New Windows sources are thin P/Invoke helpers (`WlanApi`, `GpuTemperature`) plus one cheap memory P/Invoke; the providers keep returning null for anything the OS cannot answer, and the shared views hide null rows. One generic label/value list is added to the memory view so Windows can show Windows-truthful rows without relabelling Linux metrics.

**Tech Stack:** C# 12 / .NET 8, Avalonia (compiled bindings), `System.Management`, `Microsoft.Win32.Registry`, P/Invoke to `wlanapi.dll`, `gdi32.dll`, `psapi.dll`, `iphlpapi.dll`; scratch verification console app outside the repo.

**Spec:** `docs/superpowers/specs/2026-10-27-windows-parity-data-design.md`

## Global Constraints

- Branch `fix/windows-parity-data` off `main`; one commit per task.
- Windows-only behaviour changes. Linux and Android output must stay byte-identical: every new provider member defaults to null/empty, and every new UI row renders only when its value is non-empty.
- No new NuGet dependency, no administrator rights, no kernel driver.
- A value the OS cannot answer is `null` (row hides). Never substitute a different metric to fill a row.
- No visible console window, ever: keep `<OutputType>WinExe</OutputType>`; every `Process.Start` uses `UseShellExecute = false` and `CreateNoWindow = true`.
- C# only in the repo (AGENTS.md). The verification harness stays outside the repo at `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck`.
- Existing display formats: GB to 1 dp, percent to 0 dp, temperature as `N °C`.
- `dotnet build` must end with 0 warnings and 0 errors.

## Review Focus

- A GPU driver that does not implement `KMTQAITYPE_ADAPTERTEMPERATURE` (including the A10 iGPU) must hide the temperature row — never show `0 °C`.
- Two adapters where the first already has values (NVIDIA + iGPU) must not swap utilization or memory between cards.
- No wireless adapter, or `wlansvc` stopped, must still produce a disconnected snapshot with no crash and no child process.
- WlanAPI reports a disabled adapter while a second adapter is Up — the snapshot must use the Up one.
- `GetPerformanceInfo` unavailable or memory counters unexpected must hide the Windows memory rows and never throw, and must not add a WMI query per tick.

---

### Task 1: Wi-Fi from WlanAPI, with dBm and both-protocol totals

**Files:**
- Create: `PerformanceMonitor.Shared/Providers/WlanApi.cs`
- Modify: `PerformanceMonitor.Shared/Providers/WifiProviders.cs` (Windows provider only, lines 14-169)
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs` (outside repo)

**Interfaces:**
- Consumes: `CommandRunner.Run(string, string, int)` (fallback only), `NetworkInterface.GetAllNetworkInterfaces()`.
- Produces:
  - `internal readonly record struct WlanState(string AdapterName, bool Connected, string? Ssid, string? Bssid, int? RssiDbm, string? Phy, string? Band);`
  - `internal static class WlanApi { public static WlanState? Query(); }` — `null` when there is no wireless adapter or the WLAN service cannot be opened.
  - `internal static class WifiText { public static string StandardFromPhy(string? phy); public static int? PercentFromRssi(int dbm); }`
  - `WindowsWifiProvider.GetSnapshot()` keeps its signature. `SignalStrength` becomes the Linux format `"📶 -NN dBm"` when `RssiDbm` is known and keeps `"📶 NN%"` when only the netsh fallback answered; `SignalPercentText` carries the percentage in both cases (today Windows puts the percentage in both rows, which duplicates it — see `WifiProviders.cs:153-159` and the Linux fill at `:252-253`).

- [ ] **Step 1: Write the failing formatting test**

In `paritycheck/Program.cs`, before any provider use:

```csharp
void Check(string name, object actual, object expected)
{
    if (!Equals(actual, expected)) { Console.WriteLine($"FAIL {name}: {actual} != {expected}"); Environment.ExitCode = 1; }
}

Check("rssi -50", WifiText.PercentFromRssi(-50), 100);
Check("rssi -75", WifiText.PercentFromRssi(-75), 50);
Check("rssi -100", WifiText.PercentFromRssi(-100), 0);
Check("rssi -120", WifiText.PercentFromRssi(-120), 0);
Check("phy ax", WifiText.StandardFromPhy("802.11ax"), "Wi-Fi 6 (802.11ax)");
Check("phy be", WifiText.StandardFromPhy("802.11be"), "Wi-Fi 7 (802.11be)");
Check("phy null", WifiText.StandardFromPhy(null), "802.11a/b/g");
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\paritycheck.csproj -c Release`
Expected: build error, `WifiText` does not exist.

- [ ] **Step 3: Implement `WifiText`**

`PercentFromRssi`: `Math.Clamp(2 * (dbm + 100), 0, 100)`; `StandardFromPhy`: the switch currently in `WifiProviders.cs:87-94` (`be`/`ax`/`ac`/`n`, default `"802.11a/b/g"`), matching on the `802.11` prefix.

- [ ] **Step 4: Run it to verify it passes**

Run the command from Step 2. Expected: no `FAIL` lines, exit code 0.

- [ ] **Step 5: Write the failing no-adapter snapshot test**

```csharp
var snap = new WindowsWifiProvider().GetSnapshot();
Console.WriteLine($"snapshot: {snap?.Status} | {snap?.Ssid} | {snap?.AdapterName}");
Check("snapshot present", snap is not null, true);
if (snap is { Status: "Disconnected" })
    Console.WriteLine("disconnected path ok (no adapter, or adapter not associated)");
else if (snap is not null && System.Guid.TryParse(snap.Ssid, out _))
{
    Console.WriteLine("FAIL: SSID parsed as a GUID");
    Environment.ExitCode = 1;
}
```

Run it. Expected on this machine: `snapshot: Disconnected | No wireless adapter | Wi-Fi` plus `disconnected path ok`.

- [ ] **Step 6: Implement `WlanApi.Query()`**

`WlanOpenHandle(2, ...)` → `WlanEnumInterfaces`; prefer an interface whose `isState == wlan_interface_state_connected`, else the first. For the chosen interface query `wlan_intf_opcode_current_connection` (`WLAN_CONNECTION_ATTRIBUTES`): `Ssid` decoded from `dot11Ssid.ucSSID[0..uSSIDLength]`, AP MAC as BSSID, `wlanAssociationAttributes.dot11PhyType` → `WifiText.StandardFromPhy`, `band` from `ulChCenterFrequency` (2412-2484 → `2.4 GHz`, 5000-5895 → `5 GHz`, ≥5925 → `6 GHz`). dBm from `WlanGetNetworkBssList` (`WLAN_BSS_ENTRY.lRssi`) matched on BSSID; fall back to `null` when that call fails. Return `null` on any failure or zero wireless interfaces.

- [ ] **Step 7: Route `GetSnapshot` through WlanAPI**

`Query()` non-null → build the snapshot from it (SSID/BSSID/standard/band, `SignalStrength = $"📶 {RssiDbm} dBm"`, `SignalPercentText` from `WifiText.PercentFromRssi`). `Query()` null and a wireless NIC exists → existing netsh parse path (keep it, keep its 5 s cache), so a WLAN-service failure still fills the row with `"📶 NN%"` and no dBm. No row ever shows a fabricated dBm.

- [ ] **Step 8: Replace the IPv4-only totals**

Replace `nic.GetIPv4Statistics()` with `GetIfEntry2` (`iphlpapi.dll`, `MIB_IF_ROW2.InOctets`/`OutOctets` selected by `InterfaceLuid`). Keep null on failure.

- [ ] **Step 9: Build and verify**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: 0 warnings, 0 errors. Then rerun the Step 2 command; expected exit code 0 and no `FAIL`.

- [ ] **Step 10: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/WlanApi.cs PerformanceMonitor.Shared/Providers/WifiProviders.cs
git commit -m "feat(windows): read Wi-Fi from WlanAPI, add dBm and both-protocol totals"
```

---

### Task 2: Assign GPU values to the right adapter

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/GpuProviders.cs:58-94,149-209`
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs`

**Interfaces:**
- Consumes: `GpuInfo` (`Abstractions.cs:134-152`), `Win32_PerfFormattedData_GPUPerformanceCounters_*`.
- Produces: `internal static class GpuAssign { public static void ApplyByIndex(IReadOnlyList<GpuInfo> gpus, SortedDictionary<int, float> utilization, SortedDictionary<int, (long Dedicated, long Shared)> memory); }`

- [ ] **Step 1: Write the failing fixture test**

```csharp
var nvidia = new GpuInfo { Name = "NVIDIA RTX", Utilization = 40, MemoryUsage = "1 GB / 8 GB" };
var igpu   = new GpuInfo { Name = "AMD Radeon R7" };
var gpus   = new List<GpuInfo> { nvidia, igpu };
var util   = new SortedDictionary<int, float> { [0] = 5, [1] = 7 };
var mem    = new SortedDictionary<int, (long, long)> { [0] = (1L, 0L), [1] = (2L, 0L) };
GpuAssign.ApplyByIndex(gpus, util, mem);
Check("igpu util", igpu.Utilization, 7.0);
Check("nvidia util kept", nvidia.Utilization, 40.0);
Check("igpu mem", igpu.MemoryUsage, "2 MB");
```

- [ ] **Step 2: Run it to verify it fails**

Expected: build error, `GpuAssign` does not exist.

- [ ] **Step 3: Implement `ApplyByIndex`**

For each adapter index `i`: only fill `Utilization` when `gpus[i].Utilization == null` and `utilization` has key `i`; only fill `MemoryUsage` when it is null and `memory` has key `i` (`"{used} / {total}"` when `TotalMemoryBytes` is set, else used only). No running counter, so a skipped adapter cannot shift the next one. `SamplePerfByPhys`/`SampleAdapterMemoryByPhys` return their `SortedDictionary` unchanged (stop discarding the keys at `:173`).

- [ ] **Step 4: Run it to verify it passes**

Expected: no `FAIL` lines, exit code 0. Then wire `SampleGpuList` to call `GpuAssign.ApplyByIndex` instead of the two loops at `:64-91`.

- [ ] **Step 5: Build and run on hardware**

Run the paritycheck app. Expected: the single-adapter line prints utilization and memory without exception; the disk lines unchanged.

- [ ] **Step 6: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/GpuProviders.cs
git commit -m "fix(windows): assign GPU counters by adapter index, not by running count"
```

---

### Task 3: GPU temperature for non-NVIDIA adapters

**Files:**
- Create: `PerformanceMonitor.Shared/Providers/GpuTemperature.cs`
- Modify: `PerformanceMonitor.Shared/Providers/GpuProviders.cs` (`SampleGpuList`, after the NVIDIA and counter passes)
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs`

**Interfaces:**
- Consumes: `GpuAssign` (Task 2), `GpuInfo.TemperatureC` / `TemperatureText`.
- Produces: `internal static class GpuTemperature { public static IReadOnlyList<double?> ReadAll(); }` — one entry per display adapter in enumeration order; `null` = the driver did not answer.

- [ ] **Step 1: Probe the machine (this is the task's test)**

In paritycheck:

```csharp
var temps = GpuTemperature.ReadAll();
for (int i = 0; i < temps.Count; i++) Console.WriteLine($"adapter {i} temp: {(temps[i]?.ToString("F1") ?? "unsupported")}");
```

Expected: build error first (`GpuTemperature` missing) = RED. Implement `D3DKMTEnumAdapters2` + `D3DKMTOpenAdapterFromLuid` + `D3DKMTQueryAdapterInfo` (`KMTQAITYPE_ADAPTERTEMPERATURE`, Win10 1709+) in `GpuTemperature.cs`, then run.

- [ ] **Step 2: Record the probe result and wire it**

Run the app. If an entry is a number, assign `gpus[i].TemperatureC = temps[i]` for adapters whose `TemperatureC` is still null and `TemperatureText` is null. If every entry is `null`/unsupported, the wiring still lands and the row stays hidden — record the probe output in the commit message. Never write `0`.

- [ ] **Step 3: Verify the overlay**

Publish: `dotnet publish "PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o C:\Users\nj\AppData\Local\Temp\opencode\winpublish`.
Run the exe, enable the overlay. Expected: a `GPU temp` row with the same value the GPU tab shows (the overlay binds `Gpu.Temperature`, so no XAML change is expected here — confirm it).

- [ ] **Step 4: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/GpuTemperature.cs PerformanceMonitor.Shared/Providers/GpuProviders.cs
git commit -m "feat(windows): read GPU temperature from D3DKMT for AMD/Intel adapters"
```

---

### Task 4: Say why a GPU has no utilization

**Files:**
- Create: `GpuReason` inside `PerformanceMonitor.Shared/Providers/GpuTemperature.cs`
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (`GpuInfo.DriverName`)
- Modify: `PerformanceMonitor.Shared/Providers/GpuProviders.cs` (`SampleGpuList` tail, `BuildList`)
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs`

**Interfaces:**
- Produces: `GpuReason.ForUtilization(string? driverName) => string?` returning `"Utilization not exposed by the <driver> driver"` when a driver name is known, `"Utilization not exposed by this GPU driver"` otherwise; `null` is never returned when called from the Windows path.

- [ ] **Step 1: Write the failing test**

```csharp
Check("reason named", GpuReason.ForUtilization("amdkmdag"), "Utilization not exposed by the amdkmdag driver");
Check("reason generic", GpuReason.ForUtilization(null), "Utilization not exposed by this GPU driver");
```

Expected: build error (`GpuReason` missing).

- [ ] **Step 2: Implement and assign**

Implement the two strings. In `SampleGpuList`, after all value passes, set `g.UtilizationText = GpuReason.ForUtilization(g.DriverName)` for adapters where `Utilization == null` and `UtilizationText == null`. Add `public string? DriverName { get; set; }` to `GpuInfo`, copy it in `GpuMap.Clone`, and fill it in `BuildList` from the first token of `Win32_VideoController` `InstalledDisplayDrivers` (e.g. `amdkmdag`); leave it null when absent.

- [ ] **Step 3: Run and verify**

Run the paritycheck app. Expected: no `FAIL`, exit code 0. Run the published app: an adapter without counters shows the reason text where the percentage would be, exactly like Linux's fallback.

- [ ] **Step 4: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/GpuProviders.cs PerformanceMonitor.Shared/Providers/GpuTemperature.cs PerformanceMonitor.Shared/Providers/Abstractions.cs
git commit -m "feat(windows): show why GPU utilization is unavailable"
```

---

### Task 5: Windows memory detail rows, cheap and honest

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (`MemoryUsage`)
- Modify: `PerformanceMonitor.Shared/Providers/MemoryProvider.cs:11-48`
- Modify: `PerformanceMonitor.Shared/ViewModels/MemoryViewModel.cs`
- Modify: `PerformanceMonitor.Shared/Views/MainView.axaml:169-190`
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs`

**Interfaces:**
- Produces: `public sealed record MemoryDetail(string Label, string Value);` and on `MemoryUsage`: `public IReadOnlyList<MemoryDetail> Details { get; init; } = Array.Empty<MemoryDetail>();`
- Produces: `MemoryUsage.Percent` computed from raw bytes, not from rounded GB.
- UI: `ItemsControl` bound to `Details` inside the memory row panel; renders nothing when the list is empty (so Linux is unchanged).

- [ ] **Step 1: Write the failing test**

```csharp
var usage = new HardwareInfoMemoryProvider().GetUsage();
foreach (var d in usage.Details) Console.WriteLine($"detail: {d.Label} = {d.Value}");
Check("detail count", usage.Details.Count, 4);
Check("labels", string.Join(", ", usage.Details.Select(d => d.Label).OrderBy(x => x)),
      "Cached, Commit, Nonpaged pool, Paged pool");
Check("no empty values", usage.Details.All(d => d.Value.Length > 0), true);
Check("percent sane", usage.Percent >= 0 && usage.Percent <= 100, true);
```

- [ ] **Step 2: Run it to verify it fails**

Expected: compile error (`Details` missing).

- [ ] **Step 3: Implement the counters**

Replace the `Hardware.Info` rounded math with `GlobalMemoryStatusEx` (`MEMORYSTATUSEX`, raw `ulong`) for total/available and `GetPerformanceInfo` (`PERFORMANCE_INFORMATION`, `psapi.dll`, page size × counts) for `Commit` (`CommitTotal`/`CommitLimit`), `Cached` (`SystemCache`), `Paged pool`, `Nonpaged pool`. Percent = raw used/raw total × 100, 0 dp. Keep the page-file `Win32_PageFileUsage` query but cache its text for 30 s. On any P/Invoke failure return the previous totals with an empty `Details` (rows hide; no throw).

- [ ] **Step 4: Run it to verify it passes**

Expected: four `detail:` lines, no `FAIL`, exit code 0. Percent must print the same value Task Manager shows within 1 point.

- [ ] **Step 5: Render the rows**

Add to `MemoryViewModel`: `[ObservableProperty] private IReadOnlyList<MemoryDetail> _details = Array.Empty<MemoryDetail>();` and assign `Details = m.Details` in the UI-thread post. In `MainView.axaml` after the `Pressure (PSI)` `DockPanel`, add:

```xml
<ItemsControl ItemsSource="{Binding Details}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <DockPanel>
                <TextBlock Text="{Binding Label}" Foreground="#888888"/>
                <TextBlock Text="{Binding Value}" Foreground="White" HorizontalAlignment="Right"/>
            </DockPanel>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

- [ ] **Step 6: Build and run on both OSes' build targets**

`dotnet build` (0/0), then publish win-x64 and launch: the memory tab shows Commit/Cached/Paged pool/Nonpaged pool above the cards. Publish linux-x64: expected success (proves the shared view change compiles for Linux; Linux rows are unaffected because `Details` stays empty).

- [ ] **Step 7: Commit**

```bash
git add PerformanceMonitor.Shared/Providers/Abstractions.cs PerformanceMonitor.Shared/Providers/MemoryProvider.cs PerformanceMonitor.Shared/ViewModels/MemoryViewModel.cs PerformanceMonitor.Shared/Views/MainView.axaml
git commit -m "feat(windows): show commit, cache and pool rows in the memory view"
```

---

### Task 6: NVIDIA without a CLI (conditional)

**Files:**
- Create: `PerformanceMonitor.Shared/Providers/Nvml.cs`
- Modify: `PerformanceMonitor.Shared/Providers/GpuProviders.cs` (`NvidiaSmi.Query` call sites)
- Test: `C:\Users\nj\AppData\Local\Temp\opencode\paritycheck\Program.cs`

**Interfaces:**
- Produces: `internal static class Nvml { public static IReadOnlyList<NvidiaGpuRow>? Query(); }` — `null` when `nvml.dll`/`nvmlInit_v2` is unavailable, so the caller can fall back to `nvidia-smi`.

- [ ] **Step 1: Probe for NVML**

```csharp
var rows = Nvml.Query();
Console.WriteLine(rows is null ? "nvml unavailable" : $"nvml gpus: {rows.Count}");
```

Expected on this AMD machine: `nvml unavailable` (RED is a compile error first).

- [ ] **Step 2: Decide**

If no NVIDIA machine is available to verify against, **stop here**: leave `nvidia-smi` in place (it already spawns hidden) and record the decision in the spec's Decisions section as `NVML deferred: no NVIDIA hardware to verify`. Do not ship unverified P/Invoke.

- [ ] **Step 3: Implement (only if an NVIDIA machine is available)**

`nvmlInit_v2`, `nvmlDeviceGetCount_v2`, `nvmlDeviceGetHandleByIndex_v2`, `nvmlDeviceGetName`, `nvmlDeviceGetPciInfo_v3`, `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetTemperature(NVML_TEMPERATURE_GPU)`, `nvmlDeviceGetMemoryInfo`, `nvmlSystemGetDriverVersion`. Map to `NvidiaGpuRow` exactly as `NvidiaSmi.Parse` does, then make `NvidiaSmi.Query` try `Nvml.Query()` first and fall back to the CLI.

- [ ] **Step 4: Verify and commit**

Run paritycheck on the NVIDIA machine: name/PciBus/util/temp/memory must match `nvidia-smi` output. Then commit:

```bash
git add PerformanceMonitor.Shared/Providers/Nvml.cs PerformanceMonitor.Shared/Providers/GpuProviders.cs
git commit -m "feat(windows): read NVIDIA live values from NVML instead of nvidia-smi"
```

---

### Task 7: End-to-end verification, docs, branch

**Files:**
- Modify: `docs/superpowers/specs/2026-10-27-windows-parity-data-design.md` (record probe results, deferrals)
- No source changes.

- [ ] **Step 1: Build and publish both targets**

Run the Task 3 publish command for win-x64 and the same command with `-r linux-x64 -o C:\Users\nj\AppData\Local\Temp\opencode\linuxpublish`.
Expected: both succeed; PE subsystem of the Windows exe is `2`.

- [ ] **Step 2: Prove no console window and no CLI child**

Launch the published exe, then:

```powershell
$p = Get-Process PerformanceMonitor.Desktop
Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" |
    Select-Object Name, CommandLine
```

Expected: no `conhost` entry; on a machine without Wi-Fi, no child processes at all. With Wi-Fi present, at most a hidden `netsh` only if WlanAPI failed.

- [ ] **Step 3: Confirm the data rows**

Windows: memory shows 4 detail rows; GPU card shows temperature when the driver answered; overlay shows `GPU temp` matching the tab; Wi-Fi rows unchanged where no adapter exists.
Linux build: `dotnet build` for linux-x64 and a launch on a Linux box (or the existing Linux test machine) shows the same rows it showed before this branch.

- [ ] **Step 4: Commit docs and log the deferrals**

```bash
git add docs/superpowers/specs/2026-10-27-windows-parity-data-design.md docs/superpowers/plans/2026-10-27-windows-parity-data.md
git commit -m "docs(windows): record parity sources, probe results and deferrals"
```

---

## Self-Review

- Spec coverage: every implementable row in the gap table maps to a task (GPU temp → 3, GPU keying → 2, GPU reason → 4, memory → 5, Wi-Fi/dBm/totals/no-CLI → 1, NVIDIA → 6, verification → 7). Honest limits are non-goals, not tasks.
- Step scan: each step names one action and one expected result; code bodies appear only where the mapping is not obvious from the signature.
- Type consistency: `WlanState`, `WifiText`, `GpuAssign.ApplyByIndex`, `GpuTemperature.ReadAll`, `GpuReason.ForUtilization`, `MemoryDetail`, `MemoryUsage.Details` are each defined once and consumed under the same names.
- Review Focus: five lines, each pinned to a task (1: driver-unsupported → 3; adapter swap → 2; no adapter/service → 1; disabled adapter → 1; memory counters → 5).
