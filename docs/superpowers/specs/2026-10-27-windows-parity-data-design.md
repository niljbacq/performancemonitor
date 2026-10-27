# Windows data parity — design

Date: 2026-10-27

## Intent

Make the Windows build show the same data the Linux build shows, using Linux as the
reference. Fill every gap Windows can answer with an admin-free, dependency-free API;
leave the rest null so the row hides. Also: the Windows app must never open a console
window, and no feature may depend on a command-line tool if an API exists.

Requirement (user): *"I want Windows and Linux to have the same features and data output.
Base from Linux because it's the one ahead."*

## Gap table (from the parity review)

| View | Gap | Class | Windows source |
|---|---|---|---|
| GPU | Temperature for AMD/Intel adapters (only NVIDIA via `nvidia-smi` today) | implementable | `D3DKMT_QUERYADAPTERINFO` + `KMTQAITYPE_ADAPTERTEMPERATURE` |
| GPU | Utilization/memory values can be assigned to the wrong adapter when one adapter is already filled | bug | fix keying in `WindowsGpuProvider.SampleGpuList` |
| GPU | No "why there is no utilization" text (Linux has one) | implementable | fallback string on the card |
| GPU | Total VRAM missing on UMA iGPUs (registry value often absent) | honest limit | none — DXGI reports a budget, not a size |
| Memory | Only total/used/%/swap; pool, cache, commit never shown | implementable | `GetPerformanceInfo` (`psapi.dll`) |
| Memory | Percent computed from 0.1 GB-rounded values; WMI page-file query every 1 s | bug | same P/Invoke, cached page-file text |
| Wi-Fi | dBm missing (percentage only) | implementable | WlanAPI `WLAN_BSS_ENTRY.lRssi` |
| Wi-Fi | Since-boot totals are IPv4-only | implementable | `GetIfEntry2` (`MIB_IF_ROW2`) |
| Wi-Fi | `netsh` process every 5 s (CLI dependency) | requirement | WlanAPI, `netsh` kept only as a fallback |
| CPU | Temperature, PSI, frequency residency | honest limit | needs kernel driver / admin ETW |
| Disk | I/O pressure (PSI) | honest limit | no user-mode analogue |
| CPU/GPU/Disk | Process spawning must never show a console window | requirement | `CreateNoWindow` already set; verify + keep |

Verified parity (no work): CPU utilization/specs/caches/uptime/counts, disk model/type/
capacity/used/free/samples, GPU name/kind/vendor/driver/memory-used-on-dedicated,
Wi-Fi SSID/standard/band/BSSID/IP/gateway/DNS/DHCP/netmask/rates, memory total/used/%/swap,
overlay CPU/memory/GPU rows.

## Decisions

- **API first, CLI as fallback.** Wi-Fi moves to WlanAPI; `netsh` stays only as the fallback
  when WlanAPI is unavailable, mirroring the disk classifier's IOCTL→WMI chain. NVIDIA keeps
  `nvidia-smi` until NVML is verified on real hardware; both spawn hidden.
- **Windows gets Windows-truthful memory rows, not Linux labels.** `Slab (kernel)`, `Active`,
  `Inactive` are Linux metrics; filling them with pool/standby values would violate the
  project rule against substituting a different metric. The memory view gains one generic
  label/value list that Linux leaves empty, so Linux output is byte-for-byte unchanged.
- **Missing value = null = hidden row.** A GPU driver that does not answer the temperature
  query hides the row; it never shows `0 °C`.
- **No new dependency, no admin, no kernel driver.** LibreHardwareMonitor stays rejected.
- **NVML deferred: no NVIDIA hardware to verify.** `nvidia-smi` stays as the NVIDIA
  source (it already spawns hidden, and the app has no console); an unverified
  `nvml.dll` P/Invoke would be worse than a hidden CLI. Probe on this machine:
  no NVIDIA adapter, no `nvidia-smi`, no `C:\Windows\System32\nvml.dll`.
- **Overlay** already binds `Gpu.Temperature`; once the Windows provider fills
  `TemperatureC`, the overlay shows it with no XAML change. It is verified, not re-implemented.

## Non-goals

- CPU temperature, PSI, frequency residency, disk I/O pressure on Windows.
- Total VRAM on true UMA adapters.
- Any Linux or Android behaviour change (shared UI additions must render nothing there).
- Per-core frequency / mobile-only rows.

## Verification

- Build 0 warnings/0 errors; win-x64 and linux-x64 publish.
- Scratch console app (`%TEMP%\opencode\paritycheck`) drives provider APIs and asserts.
- Published exe: no console window, no `conhost.exe` child, overlay shows GPU temperature
  when the driver answers.

## Verification results

Machine: Windows 10 19045, AMD A10-7870K (Radeon R7 iGPU), 16 GB RAM (7 GB usable),
no wireless adapter, no NVIDIA GPU.

- **Wi-Fi** — no adapter and `wlansvc` stopped, so only the none/fallback path was
  exercised: `GetSnapshot()` returns `Disconnected | No wireless adapter`,
  `WlanApi.Query()` returns null. Marshal sizes asserted against the SDK:
  `WlanInterfaceInfo` 532, `WlanBssEntry` 336, `WlanConnectionAttributes` 104.
  The connected path (SSID/BSSID/dBm/band from WlanAPI) is **unverified**; it needs a
  machine with a wireless adapter.
- **GPU temperature** — `D3DKMTQueryAdapterInfo` type **62** (`KMTQAITYPE_ADAPTERPERFDATA`)
  returns `STATUS_SUCCESS`; `Temperature` is tenths of °C at offset 56. Types 61, 63, 64
  and the plan's guessed 45 all return `0xC000000D`. Live: adapter 0 (Radeon R7) 49–64 °C;
  adapter 1 (Microsoft Basic Render) unsupported → null → row hidden.
  `Sample().TemperatureC` carries the value the overlay already binds.
- **GPU counters** — one adapter sampled at `util = 5–26%`, `mem ≈ 626–693 MB`; row count
  equals the adapter list. Multi-GPU index ordering unverified (only one real adapter).
- **GPU reason** — text function tested named and generic. The R7 does expose GPU Engine
  counters, so the reason row is not visible on this machine.
- **Memory** — `Commit 7.1 GB / 12.2 GB`, `Cached 1.4–1.6 GB`, `Paged pool 319–320 MB`,
  `Nonpaged pool 167–169 MB`. Percent 74% matched `Win32_OperatingSystem` 74% at the same
  moment; it is now computed from raw bytes, not rounded GB.
- **No console** — published win-x64 exe has PE subsystem 2 (GUI); launched for 6 s with
  0 child processes.
- **Publish** — win-x64 and linux-x64 both succeed.

Not verifiable here: connected Wi-Fi, on-screen confirmation of the overlay and memory rows
(the app opens on Home and needs clicks), and Linux output on a Linux box. The shared
changes are Windows-only additions; `Details` defaults to empty, so the Linux view renders
nothing new.
