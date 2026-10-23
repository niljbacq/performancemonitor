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

Download the latest files from [Releases](https://github.com/niljbacq/performancemonitor/releases/latest). Every download is the app itself — no zip, no extraction.

### Windows

1. Download `PerformanceMonitor-Windows.exe`.
2. Run it.
3. If Windows SmartScreen shows "Windows protected your PC", click **More info → Run anyway**. The binary is unsigned.

### Linux

- **Ubuntu / Debian:** download `PerformanceMonitor-Linux.deb`, open it, and install. The app then appears in your applications menu.
- **Portable:** download `PerformanceMonitor-Linux`, make it executable once, and run it:
  ```bash
  chmod +x PerformanceMonitor-Linux
  ./PerformanceMonitor-Linux
  ```

No extra GPU tool is required: Radeon utilization is read directly through the kernel DRM interface, without `sudo`.

Use the **Overlay** card on Home for a compact always-on-top panel. Linux X11 supports this; Wayland compositors may ignore the always-on-top hint. On Android, the card requests “Display over other apps” and opens a floating overlay that keeps updating in the background. The FPS row is the desktop overlay's render rate, not a game's FPS; game FPS needs a graphics-API hook.

### Android

1. Download `PerformanceMonitor-Android.apk`.
2. Open the file and allow installation from this source when Android asks.
3. Open **Performance Monitor** from your app list.
