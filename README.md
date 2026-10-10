# Performance Monitor

A cross-platform hardware monitoring app for Windows, Linux, and Android. It reads real-time CPU, GPU, Memory, Disk, and WiFi data and displays it in a desktop-class interface.

**This is an educational / portfolio project.** It is not published to any app store, has no monetization, and is intended for personal use and learning.

---

## What it does

Performance Monitor reads hardware data through platform-specific APIs and shows it in one UI:

- **CPU** — name, cores, live utilization, per-core frequencies (Android), cache sizes (Windows/Linux)
- **GPU** — model, vendor, driver version, utilization, temperature, VRAM
- **Memory** — total, used, percentage, swap/ZRAM (Android)
- **Disk** — model, capacity, live read/write speed, SSD/HDD split into separate columns
- **WiFi** — SSID, signal, standard, DHCP/gateway/DNS, live throughput

The app **never fakes data**. If a platform blocks access to a value (for example, Android blocks system-wide CPU utilization since Android 8), the app shows "Not available on Android" rather than a wrong number.

---

## Screenshots

> *Add screenshots here once captured. Suggested layout:*
> - Windows desktop — Disk tab (SSD + HDD columns)
> - Linux desktop — GPU tab
> - Android portrait — Home, CPU, Memory, WiFi tabs

---

## Install

### Windows

1. Download `performance-monitor-windows-x64.zip` from the [latest Actions run](https://github.com/niljbacq/performancemonitor/actions).
2. Extract the zip.
3. Run `PerformanceMonitor.exe`.
4. If Windows SmartScreen shows "Windows protected your PC", click **More info → Run anyway**. The binary is unsigned.

### Linux

1. Download `performance-monitor-linux-x64.zip` from the [latest Actions run](https://github.com/niljbacq/performancemonitor/actions).
2. Extract:
   ```bash
   unzip performance-monitor-linux-x64.zip -d performancemonitor
   cd performancemonitor
