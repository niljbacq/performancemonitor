# Windows fixes and parity — design

Date: 2026-10-27

## Intent

The Windows build is behind the Linux build in the detail rows it can fill. Bring
it to parity where Windows honestly provides the same data, and keep it
dependency-free and admin-free. Also fix three Windows-only defects in the
released `PerformanceMonitor-Windows.exe`:

1. Launching the exe opens a console window alongside the app.
2. SSD/HDD detection is inverted: the SSD lands in the HDD card and the real
   HDD never appears.
3. The app is missing from Start menu search.

Linux and Android are unchanged and out of scope.

## Root causes (verified on the user's machine)

**a. Console window.** `PerformanceMonitor.Desktop.csproj` uses
`<OutputType>Exe</OutputType>`, so the Windows PE subsystem is console.

**b. SSD/HDD.** `SeekPenaltyClassifier` reads `IncursSeekPenalty` at byte
offset 4 of the `IOCTL_STORAGE_QUERY_PROPERTY` output. The current
`DEVICE_SEEK_PENALTY_DESCRIPTOR` is `Version (4), Size (4),
IncursSeekPenalty (1)`; the flag is at offset 8. Offset 4 holds `Size = 12`,
which is non-zero, so every disk is classified HDD. The HDD card then picks
the system disk (the SSD) via `PickPrimary`, and the real HDD is dropped.

Verified by direct IOCTL: Samsung 850 PRO returns `0C 00 00 00 | 0C 00 00 00 |
00`, Toshiba DT01ACA100 returns `... | 01`.

**c. Start menu.** The shortcut *is* created at
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Performance Monitor.lnk` and
Windows lists it in its app database. The runtime creation does not notify the
shell, and the code never refreshes an existing shortcut, so the Start menu
cache and stale targets can leave the entry missing or broken. A shortcut is
created only once; re-downloading the exe elsewhere leaves it pointing nowhere.

## Approach chosen

The user asked for the most reliable option: keep both detection paths.

- **(a)** Change `OutputType` to `WinExe`. Ignored on Linux.
- **(b)** Parse the descriptor with its documented layout
  (`Version`, `Size`, `IncursSeekPenalty`) instead of a hardcoded offset.
  Keep the `MSFT_PhysicalDisk` fallback for when the IOCTL is unavailable.
- **(c)** Always rewrite the shortcut on launch (self-heals a moved exe), then
  call `SHChangeNotify(SHCNE_ASSOCCHANGED, ...)` so the shell re-reads apps
  immediately.

Alternative rejected: deleting the IOCTL path and trusting only
`MSFT_PhysicalDisk` — less code, but some SSDs report `MediaType = 3` (HDD)
and the WMI provider can be unavailable.

## Parity goals

Same shared UI, same rows, same meaning: each platform fills every row its OS
can truthfully answer and leaves the rest null (the view already hides null
rows). Android is out of scope. Linux code is the reference and does not change.

| Area | Windows today | Windows after | Source |
|---|---|---|---|
| Wi-Fi extras (SSID, standard, signal %, BSSID, gateway, DHCP, DNS 1/2, netmask, totals since boot) | None (`GetSnapshot()` unimplemented) | All rows | `netsh wlan show interfaces` + `NetworkInterface` + `Win32_NetworkAdapterConfiguration` |
| CPU speed | Max clock only | Live clock, falls back to max | `Win32_PerfFormattedData_Counters_ProcessorInformation.ProcessorFrequency` |
| CPU temperature, PSI pressure, frequency residency | None | Still none, rows hidden | No Windows API; would need a kernel-driver library |
| GPU temperature (AMD/Intel) | None | Still none, row hidden | No Windows API; `nvidia-smi` path already exists |
| Memory Active/Inactive/Slab/Pressure | None | Still none, rows hidden | Linux `meminfo`/PSI only; Windows counters are different metrics |
| Disk I/O pressure | None | Still none, row hidden | Linux PSI only; queue length is a different measure |

Decision: do **not** add LibreHardwareMonitor or any other dependency. It loads
a kernel driver, often needs administrator rights, and would only buy the
temperature rows. The user's requirement is feature parity where the OS can
answer, not an identical metric list by any means.

Both Wi-Fi and CPU changes live in the shared provider layer. When a value is
unavailable (no adapter, `wlansvc` stopped, counter missing) the provider
returns null for that field and the row stays hidden — it never substitutes a
different metric or throws.

## Non-goals

- Per-disk capacity when some partitions are unmounted (the HDD card reports
  only the mounted 512 GB of the 1 TB disk). Existing behaviour, unrelated.
- Desktop or taskbar shortcuts.
- Linux/Android changes.
- CPU/GPU temperature, PSI, cpufreq residency, and kernel memory breakdown on
  Windows (see the parity table).
- Changing the shared UI: all target rows already exist and bind to fields that
  are currently null on Windows.

## Verification

- Build the desktop project for `win-x64`; run the published exe and confirm
  no console window appears.
- A throwaway console app outside the repo referencing `PerformanceMonitor.Shared`
  prints `GetDiskList()`: interactively expect Toshiba → HDD, Samsung → SSD.
- Run the app from a new folder; confirm the `.lnk` is rewritten to the new
  target and still resolves.
- Same throwaway app prints `GetSnapshot()` and CPU `Speed`: Wi-Fi fields match
  what Windows shows for the adapter; CPU speed changes between idle (~1.7 GHz)
  and load (~3.9 GHz) instead of always reading 3.90 GHz.
- No test project exists in the repo, so hardware checks above are the tests.
  A fabricated-buffer unit test for the seek-penalty layout would prove less
  than the two real disks on this machine.
