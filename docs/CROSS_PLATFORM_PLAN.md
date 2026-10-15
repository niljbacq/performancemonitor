# Cross-Platform Feature Parity Plan — Performance Monitor

Branch: `fix/cross-platform-hardware` · Date: 2026-10-13
Goal: close the feature gap so Android and Linux expose every Windows capability that the OS actually makes available — and honestly mark the rest as "not available on this platform" instead of hiding rows.

Rule for this work: **the agent only edits files; you run every build/run command yourself** (commands listed in each section).

---

## 0. Current state (verified in code)

| Area | Windows | Linux (desktop) | Android |
|---|---|---|---|
| CPU util / speed / per-core freq | Full (PerfCounter) | Works after fixes | Util blocked by SELinux since Android 8 (`/proc/stat` denied to non-system apps); per-core freq works |
| CPU specs (cache, sockets, virtualization) | Full (WMI) | Full after lscpu parsing | Specs work (soc name, cores, min/max freq, governor, ABIs) |
| Memory total/used/% | Works | Works (Hardware.Info) | Works |
| Memory **Swap / ZRAM / Slab / Active / Inactive rows** | Missing | **Missing** (rows hidden) | **Works** (`AndroidMemoryProvider` parses `/proc/meminfo`) |
| Disk list + live I/O | Full | Works (`/proc/diskstats` + DriveInfo used/free) | List works; live I/O needs root |
| GPU name / temp / driver | Full (WMI + nvidia-smi) | Name, hwmon temp, driver name work; **utilization N/A on radeon** | EGL strings only |
| Wi-Fi details + snapshot (SSID, signal %, DHCP, traffic) | Full | **Details only; snapshot rows hidden** | Full (WifiManager) |

The single most visible parity bug: **Swap/Slab/Active/Inactive rows only appear on Android** because Linux desktop uses `HardwareInfoMemoryProvider` (total/available only), while Android already parses `/proc/meminfo`.

---

## 1. Linux Memory — add Swap / ZRAM / Slab / Active / Inactive rows (highest impact)

### Why it's missing
`ProviderFactory.CreateMemory()` (PerformanceMonitor.Shared/Providers/Abstractions.cs:275-282) gives Android a real provider but Linux/Windows get `HardwareInfoMemoryProvider` (MemoryProvider.cs, 23 lines) which only exposes total/available. `MemoryViewModel.cs:26-29` binds `SwapText / ActiveText / InactiveText / SlabText` and MainView.axaml:757-760 hides rows when those strings are empty — so Linux hides them all.

### Implementation (edits, then you build)
1. **New file** `PerformanceMonitor.Shared/Providers/LinuxMemoryProvider.cs`:
   - Read `/proc/meminfo` (same fields `AndroidMemoryProvider` already uses, Android/Providers/AndroidProviders.cs:212-270):
     - `MemTotal` / `MemAvailable` → TotalGB / UsedGB / Percent
     - `SwapTotal` / `SwapFree` → `SwapText` — format `"X.X GB / Y.Y GB (NN%)"`, or `"Not in use"` when SwapTotal == 0
     - `Active` / `Inactive` / `Slab` → those texts via the same kB→GB formatting
   - ZRAM: parse `/proc/swaps` for a device whose name contains `zram`; if found, read `/sys/block/zram0/mem_used_total` (+ `disksize`) and fold into `SwapText` as `"ZRAM 512 MB used"` (zram0 index from the swap line, not hardcoded).
2. **Factory change** `Abstractions.cs` (CreateMemory): route Linux → `LinuxMemoryProvider` instead of `HardwareInfoMemoryProvider`. (Keep Windows on Hardware.Info; optionally extend Windows later with WMI swap — see §7.)
3. No ViewModel/XAML change needed — the rows already exist and will light up.

### Verify (you run)
```
cd '/home/neil/Documents/performance monitor/performancemonitor'
/home/neil/dotnet10/dotnet build -c Debug
DOTNET_ROLL_FORWARD=Major /home/neil/dotnet10/dotnet run --project PerformanceMonitor.Desktop -c Debug --no-build
```
Expected: Memory tab now shows **Swap**, **Active**, **Inactive**, **Slab** rows with real values (and ZRAM if your box uses zram — check `cat /proc/swaps`; a swap **file** on sda2 will show as the swap device name, which is correct).

---

## 2. Linux Wi-Fi — implement `GetSnapshot()` so SSID / signal % / DHCP / traffic rows appear

### Why it's missing
`LinuxWifiProvider` (Shared/Providers/WifiProviders.cs:35-86) implements only `GetDetails()` (nmcli/iwconfig). `GetSnapshot()` returns the interface default `null` (Abstractions.cs:165-171), so `WifiViewModel.cs:79-164` skips the rich snapshot branch and falls back to the plain `NetworkInterface` branch — SSID, BSSID, signal %, gateway, DNS, netmask rows stay hidden.

### Implementation (edits, then you build)
In `WifiProviders.cs`, implement `LinuxWifiProvider.GetSnapshot()`:
- Interface + connected check: pick the `wlan* / wlp* / wlx*` interface that is `Up` and has a link (reuse the same detection the ViewModel's fallback uses).
- **SSID / BSSID / freq / signal**: parse `iw dev <iface> link` (SSID, freq) and `iw dev <iface> station dump` (station signal dBm). Convert dBm → percent: clamp `100 + dBm + 90` (Windows-style mapping) into 0-100.
- **IPv4/IPv6 + netmask**: `ip -4 addr show dev <iface>` / `ip -6 addr show dev <iface>` (or parse `/proc/net/if_inet6`).
- **Gateway**: first default route line of `/proc/net/route` (field 3, hex → dotted quad).
- **DNS**: parse `/etc/resolv.conf` `nameserver` lines (first two → Dns1/Dns2).
- **DHCP server**: skip (not exposed readably on stock Linux; leave null → row hidden, honest).
- **Traffic**: `GetIPv4Statistics().BytesReceived/BytesSent` for Download/Upload texts (same math the Android provider uses with `TrafficStats`).
- Fall back to returning `null` (current behavior) when `iw`/`ip` are unavailable.

### Verify (you run)
```
cd '/home/neil/Documents/performance monitor/performancemonitor'
/home/neil/dotnet10/dotnet build -c Debug
DOTNET_ROLL_FORWARD=Major /home/neil/dotnet10/dotnet run --project PerformanceMonitor.Desktop -c Debug --no-build
```
Expected (when connected to Wi-Fi): SSID, signal strength %, IPv4, gateway, DNS rows populate; on ethernet the existing "Wi-Fi Adapter Not Found" state stays.

---

## 3. Linux GPU — honest utilization story for radeon/amdgpu

### Why utilization is missing on your box
Your AMD A10-7870K uses the **radeon** driver. Verified: no `/sys/class/drm/card1/device/gpu_busy_percent` (amdgpu-only), no `mem_info_*`, and `/sys/kernel/debug/dri` is inaccessible without debugfs mounted. Name, hwmon temperature, and driver name all work now.

### Implementation
1. **No code change needed for temp/name** — already fixed (symlink resolution + hwmon + driver version).
2. **Make utilization available where the kernel allows it** — radeon exposes load only via debugfs (`radeon_pm_info`), which is unreadable unless debugfs is mounted. The code already tries `amdgpu_pm_info` then `radeon_pm_info` (GpuProviders.cs:612-672). On your box this needs:
   ```
   sudo mount -t debugfs none /sys/kernel/debug
   ```
   (temporary; add to /etc/fstab to persist). After mounting, the existing loader should read `GPU Load`/`temp` and populate utilization + temperature.
3. **UI honesty improvement (small edit)**: in `GpuViewModel`/`GpuSubViewModel`, when a GPU is present but `UtilizationText` is null after sampling, show `"Not available (radeon driver)"` instead of a flat 0% — so a missing value is clearly an OS/driver limit, not "idle". Same treatment for `MemoryUsage` (`"N/A (amdgpu only)"`).
4. Optionally: for amdgpu systems, `gpu_busy_percent` + `mem_info_vram_used/total` already work with zero extra code (verified in FillSysfsSample, GpuProviders.cs:587-610).

### Verify (you run)
```
sudo mount -t debugfs none /sys/kernel/debug
cat /sys/kernel/debug/dri/0/radeon_pm_info   # or 1/2... find the index: ls /sys/kernel/debug/dri/
```
Then rerun the desktop app — utilization/temp should appear once debugfs is readable. If it still reads empty, the fallback text from step 3 makes the limit visible.

---

## 4. Android — honest CPU + best-effort additions

### What cannot change (OS-imposed)
- **System-wide CPU %**: blocked by SELinux since Android 8 for non-system apps (`/proc/stat` denied). The code already returns `"Not available on Android"` — keep that.
- **Disk live I/O**: needs root (`/proc/diskstats` denied). Keep list-only.
- **GPU utilization/temp**: no public HAL surface on MediaTek/Mali without root. EGL strings are the honest ceiling.
- What DOES work and already shows: memory incl. swap/slab rows, per-core freq, WiFi snapshot, disk list.

### Implementation (small edits, then you build)
1. **Per-app disk I/O (no root)**: add a read of `/proc/self/io` (read_bytes/write_bytes) sampled every tick → show as `"This app: X KB/s read / Y KB/s write"` in the Android Disk view, labeled clearly, alongside the root-only system row. This is the one Android-available metric Windows has that the app currently doesn't show.
2. **GPU thermal best-effort**: in `AndroidGpuProvider.GetGpuList()`, after EGL, scan `/sys/class/thermal/thermal_zone*/type` for a zone whose type contains `gpu`/`mtktsgpu`; if found, read `temp` (milli-°C → °C) into `TemperatureC`. Many MediaTek devices expose this even without root. No result → stays "N/A" (honest).
3. **CPU cluster info**: already present via `ClusterText` (`GetSpecs`, AndroidProviders.cs:69-112) — no change.

### Verify (you run)
```
cd '/home/neil/Documents/performance monitor/performancemonitor'
/home/neil/dotnet10/dotnet build PerformanceMonitor.Android -c Debug -f net10.0-android
adb install -r /home/neil/Documents/performance\ monitor/performancemonitor/PerformanceMonitor.Android/bin/Debug/net10.0-android/com.performancemonitor.app-Signed.apk
```
(Adjust APK path to whatever `dotnet build` prints.)

---

## 5. Linux disk — per-device swap flag (remove the "Page file: Yes everywhere" artifact)

### Why it's wrong
`LinuxDiskProvider.BuildDiskList` sets `PageFile = hasSwap ? "Yes" : "No"` where `hasSwap` is any-swap-on-system (DiskProviders.cs:239-286), so both sda and sdb show "Yes" even though only one hosts swap.

### Implementation (edit, then you build)
In `BuildDiskList`: read `/proc/swaps` once, collect swap device names (e.g. `/dev/sda2`, `/dev/zram0`), and set `PageFile = swapDevices.Any(d => d == dev || d.StartsWith(dev)) ? "Yes" : "No"`. Non-swap disks then correctly show "No".

### Verify (you run)
`cat /proc/swaps` first — expect `/dev/sda2` (your `/home`). After rebuild, only sda shows Page file: Yes.

---

## 6. Linux CPU — replace "Handles: N/A" with a real open-files metric

Windows shows Handles (kernel object count). Linux has no direct equivalent, but open file descriptors are a close, useful analog:
- In `LinuxCpuProvider` (or `GetProcessStats`), read `/proc/sys/fs/file-nr` (allocated, unused, max) → `"Open files: N  (max M)"` into the field currently showing "N/A". If the ViewModel binds Handles to a fixed label, add the value to `ProcessStats`/the existing stats text and leave the Windows-only label untouched.

### Verify (you run)
Rebuild + run; CPU tab shows an open-files count instead of N/A.

---

## 7. Windows — finish swap/ZRAM parity (optional, low priority)

- Windows swap = page file. `HardwareInfoMemoryProvider` doesn't expose it; add a WMI query (`Win32_PageFileUsage`) or a PerfCounter ("Paging File", "% Usage") to populate `SwapText` — same row the Android/Linux providers feed. ZRAM is Linux-specific; leave "Not in use" on Windows.

---

## 8. Acceptance checklist (what "all features available" means)

| Row | Linux after this plan | Android after this plan |
|---|---|---|
| CPU util/speed/per-core | ✅ works | util: honest "Not available" (OS limit) |
| CPU specs | ✅ | ✅ |
| Memory rows incl. **Swap/Slab/Active/Inactive** | ✅ new | ✅ |
| Disk list + live I/O | ✅ | list ✅, system I/O "requires root", **per-app I/O new** |
| Disk used/free | ✅ | ✅ (external storage) |
| GPU name/temp/driver | ✅ | name ✅, temp best-effort, util "N/A" |
| GPU utilization | ✅ **when debugfs mounted** (radeon) / amdgpu native | honest "N/A" |
| Wi-Fi details + snapshot | ✅ new snapshot | ✅ |
| Handles analog | ✅ open-files | n/a |

Commands to run after edits (all sections at once):
```
cd '/home/neil/Documents/performance monitor/performancemonitor'
/home/neil/dotnet10/dotnet build -c Debug
DOTNET_ROLL_FORWARD=Major /home/neil/dotnet10/dotnet run --project PerformanceMonitor.Desktop -c Debug --no-build
/home/neil/dotnet10/dotnet build PerformanceMonitor.Android -c Debug -f net10.0-android
adb install -r <path-to-apk-from-build-output>
```
Nothing in this plan is committed; all changes stay in the working tree on `fix/cross-platform-hardware` until you review.

---

## 9. Round-2 enhancements (from the Claude review)

### Linux

| Feature | Where | Notes / verification |
|---|---|---|
| CPU temperature | `LinuxCpuProvider.ReadCpuTemperature` | hwmon `k10temp` (AMD) / `coretemp` (Intel) / `fam15h_power`; also prints `fam15h_power` watts. Verify: `grep -H . /sys/class/hwmon/hwmon*/name` |
| ZRAM | `LinuxMemoryProvider.FormatSwap` | reads `/sys/block/zram*/mm_stat` (columns: orig, compr, mem_used); the old per-file `orig_data_size` no longer exists on newer kernels. Verify: `cat /sys/block/zram0/mm_stat; zramctl` |
| PSI pressure | `LinuxPressure` → CPU/Memory/Disk rows | `/proc/pressure/{cpu,memory,io}`. Verify: `cat /proc/pressure/cpu` |
| GPU utilization, no debugfs | `LinuxGpuProvider.TryRadeontopUtilization` | last resort: `radeontop -d - -l 1` (reads the GPU busy register over DRM; no root/debugfs). Honest reason string when radeontop is absent. |
| Frequency residency | `LinuxCpuProvider.FrequencyResidencyText` | cpufreq `time_in_state`, top-3 P-states. Verify: `cat /sys/devices/system/cpu/cpu0/cpufreq/stats/time_in_state` |

**radeon busy register / debugfs:** udev cannot chmod debugfs nodes (they are created dynamically). Keep the documented manual step in the honest string: `sudo mount -t debugfs none /sys/kernel/debug`, then read `.../dri/*/radeon_pm_info`. Kernel lockdown can block debugfs entirely.

**Optional: try amdgpu on Kaveri (test, not required).** Kaveri (CIK) can run `amdgpu` instead of `radeon`. Add to the kernel command line and reboot:
```
amdgpu.cik_support=1 radeon.cik_support=0
```
Then `gpu_busy_percent`, `mem_info_vram_*` and `pp_dpm_sclk` appear and the existing amdgpu code paths light up with no new code. Revert by removing the parameters.

### Android

| Feature | Where | Notes / verification |
|---|---|---|
| Thermal fallbacks | `AndroidGpuProvider.ReadFallbackThermal` | battery temperature (sticky `ACTION_BATTERY_CHANGED`), `PowerManager.GetThermalHeadroom` (API 30+), `CurrentThermalStatus` (API 29+). GPU zone match broadened to gpu/g3d/gpuss/mali/kgsl. |
| Frequency residency | `AndroidCpuProvider.FrequencyResidencyText` | `cpufreq/stats/time_in_state`. Verify: `adb shell cat /sys/devices/system/cpu/cpu0/cpufreq/stats/time_in_state` |
| Own-app CPU | `AndroidCpuProvider.AppUsageText` | `/proc/self/stat` utime+stime delta, shown as % of one core. |
| Foreground service | `MonitorForegroundService` + manifest | type `specialUse` (no 6-hour `dataSync` timeout), channel + `POST_NOTIFICATIONS` runtime request. Verify: `adb shell dumpsys activity services com.performancemonitor.app` |

For Google Play submission, add the special-use subtype property to the service element:
`<property android:name="android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE" android:value="hardware performance monitoring"/>`.

### Windows

| Feature | Where | Notes |
|---|---|---|
| Any-vendor GPU memory | `WindowsGpuProvider.SampleAdapterMemoryByPhys` | `GPU Adapter Memory` counters (`Dedicated Usage` / `Shared Usage`), grouped by `phys_N`. Verify: `Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage'` |
| Total VRAM > 4 GB | `ReadDisplayAdapterTotalBytes` | registry `HardwareInformation.qwMemorySize` (64-bit REG_BINARY), matched by `MatchingDeviceId`. Avoids `AdapterRAM`'s uint32 cap. |

### Cross-platform

- **System Details page** (`SystemInfoProvider` / `SystemViewModel`): OS, arch, process arch, .NET runtime, Avalonia version, kernel, distro, machine name, processor count; Android adds manufacturer/model/hardware/board/SoC/API level.
- **SamplingService + CSV** (`SamplingService`): one background loop samples all providers every 2 s and writes UTC ISO-8601 CSV (`null` → empty cell), flushed every ~5 s. Log dir: `LocalApplicationData/PerformanceMonitor` on desktop, `GetExternalFilesDir(null)` on Android.

### Round-2 commands

```
cd '/home/neil/Documents/performance monitor/performancemonitor'
/home/neil/dotnet10/dotnet build -c Debug
DOTNET_ROLL_FORWARD=Major /home/neil/dotnet10/dotnet run --project PerformanceMonitor.Desktop -c Debug --no-build
/home/neil/dotnet10/dotnet build PerformanceMonitor.Android -c Debug -f net10.0-android
adb install -r PerformanceMonitor.Android/bin/Debug/net10.0-android/com.performancemonitor.app-Signed.apk
```

