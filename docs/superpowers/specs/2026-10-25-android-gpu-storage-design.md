# Android GPU and Storage tabs — design

Date: 2026-10-25
Status: awaiting approval (approach picked: GPU 1, Storage 1 + 2)
Scope: Android mobile layout only. Desktop and the overlay are untouched.

## Intent

Fill the Android GPU and Storage tabs with real values that the normal app sandbox can read. No guessed numbers, no root, no Shizuku, no privileged permissions. A row with no source stays hidden.

## What is impossible (measured on the Infinix X6885, Android 16)

These were probed with `adb shell` and are denied to the app as well:

- GPU load, temperature, clock, and memory: `thermal_zone*`, Mali `gpuinfo`/`utilization`, kgsl, `devfreq` all absent or permission-denied.
- Device-wide disk I/O: `/proc/diskstats` and `/sys/block/*` are permission-denied.
- Another app's rendered FPS: needs SurfaceFlinger access.

So the GPU tab must not show utilization, temperature, or memory on this device. Battery temperature is not GPU temperature and must leave the GPU tab. The Storage tab must not show read/write/response rows.

## Approaches considered

- GPU 1 — graphics and display facts plus thermal status. Picked.
- GPU 2 — static GL capability sheet. Rejected: nothing live.
- GPU 3 — this app's own graphics memory and frame time. Rejected: measures the monitor, not the phone.
- Storage 1 — volumes, filesystem, reclaimable space, free-space trend. Picked.
- Storage 2 — what is using the space. Picked, limited as described below.
- Storage 3 — on-demand write/read benchmark. Rejected: writes flash and measures one moment.

## GPU tab

### GPU identity

From the existing EGL probe (`GpuInfoEgl`), unchanged:

- Name = `GL_RENDERER` (for example `Mali-G57 MC2`).
- Vendor = `GL_VENDOR`.
- Graphics API = truncated `GL_VERSION` (`OpenGL ES 3.2`) joined with Vulkan, e.g. `OpenGL ES 3.2 · Vulkan 1.3`.
  - Vulkan comes from `PackageManager` feature `android.hardware.vulkan.version`, decoded as `major.minor(.patch)`.
  - If the feature is absent, only the GLES text is shown.
- Driver version = the driver build inside `GL_VERSION`, i.e. the part after ` v` (`v1.r38p1-...`). Null if there is no ` v` marker.
- Keep the best-effort `TryReadGpuUtilization()` for other OEMs; it stays null here.
- Delete `ReadGpuTemperature()` and `ReadFallbackThermal()`. `TemperatureText` stays null, so the Temperature row hides. The overlay's Battery row is unchanged.

### Display and thermal

New shared contract, implemented only by `AndroidGpuProvider`:

```csharp
public sealed class GpuEnvironment
{
    public string? DisplayResolution { get; set; } // "1080 × 2400"
    public string? RefreshRate { get; set; }       // "60 Hz (max 144 Hz)"
    public string? Hdr { get; set; }               // "HDR10, HLG" or "No"
    public string? WideColor { get; set; }         // "Yes" / "No"
    public string? ThermalStatus { get; set; }     // "None" / "Light" / ...
}

public interface IGpuEnvironmentProvider
{
    GpuEnvironment GetEnvironment();
}
```

Sources:

- Resolution: `Display.GetRealMetrics()`.
- Refresh rate: `Display.RefreshRate` plus the highest `Display.GetSupportedModes()` rate (API 23+); current only if modes are unavailable.
- HDR: `Display.HdrCapabilities.SupportedHdrTypes` (API 24+) mapped to names, `No` when empty.
- Wide colour: `Display.IsWideColorGamut` (API 26+).
- Thermal: `PowerManager.CurrentThermalStatus` (API 29+) mapped to the platform names.

`GpuViewModel` gains `DisplayResolution`, `RefreshRateText`, `HdrText`, `WideColorText`, `ThermalStatus`, and `HasEnvironment`. It calls the interface once after specs, then again on each existing 2 s tick (these calls are cheap). When the provider does not implement the interface, all stay null and the card hides.

Layout, after the Integrated card:

- A `Display` card: Resolution, Refresh rate, HDR, Wide colour. Rows hidden when null.
- A `Thermal` card: Status.
- One `mSub` note: `GPU load and temperature are not exposed by Android.`

## Storage tab

### Live capacity

`AndroidDiskProvider` implements `IDiskListProvider` in addition to `IDiskProvider`:

- `GetDiskList()` and `SampleDiskList()` both return one `DiskInfo` (the current internal-storage numbers, every 2 s). The existing `GetSpecs()` maps that item.
- `DiskInfo` gains `double? FreeBytes`.
- `DiskSubViewModel.UpdateSample(DiskInfo)` copies `UsedText` and `FreeText` when non-null, so the visible numbers move; `DiskViewModel` reads `FreeBytes` from the same sample for the trend. Desktop list providers are unaffected.
- `DiskViewModel` keeps free-space samples for 5 minutes and exposes `FreeSpaceTrendText` (`−120 MB in 4 min`, `+80 MB in 2 min`, `No change in 5 min`). Hidden until there are two samples.

### Volumes, filesystem, reclaimable

New shared contract, implemented by `AndroidDiskProvider`:

```csharp
public sealed class StorageExtras
{
    public string? FileSystem { get; set; }   // "f2fs (read-write)"
    public string? BlockSize { get; set; }    // "4 KB"
    public string? Reclaimable { get; set; }  // "1.20 GB"
    public IReadOnlyList<StorageVolumeInfo> Volumes { get; set; } = [];
    public StorageBreakdown? Breakdown { get; set; }
}

public sealed class StorageVolumeInfo
{
    public string Name { get; set; } = "";
    public string UsageText { get; set; } = ""; // "12.4 GB of 64 GB"
    public double UsedPercent { get; set; }
}

public sealed class StorageBreakdown
{
    public string Apps { get; set; } = "";
    public string AppData { get; set; } = "";
    public string AppCache { get; set; } = "";
    public string? Photos { get; set; }    // null = media permission not granted
    public string? Videos { get; set; }
    public string? Audio { get; set; }
    public string Other { get; set; } = "";
}

public interface IStorageExtrasProvider
{
    StorageExtras GetExtras();
    void OpenUsageAccessSettings();
    void RequestMediaAccess();
}
```

Sources:

- File system: parse `/proc/mounts` for `/data`; report the type and `read-write` unless the options contain `ro`. Null if unreadable.
- Block size: `StatFs("/data").BlockSizeLong`.
- Reclaimable: `GetAllocatableBytes(UuidDefault) − GetFreeBytes(UuidDefault)`, floored at zero (API 26+). `GetAllocatableBytes` already includes ordinary free space; showing it raw would label almost all free space as reclaimable.
- Volumes: `StorageManager.StorageVolumes`, skipping the primary volume (already the Internal Storage card). Each remaining mounted volume gets its description, used/total, and percent from `StorageStatsManager.GetTotalBytes()` / `GetFreeBytes()` (API 26+, no Usage Access). Below API 26, or when the list is empty, the card hides. No permission is involved.

### What is using the space

Requires `PACKAGE_USAGE_STATS` (Usage Access). Added to the manifest with `tools:ignore="ProtectedPermissions"`; it is granted in Settings, not by a runtime dialog.

- Check with `AppOpsManager.CheckOpNoThrow("android:get_usage_stats", Process.MyUid(), PackageName)`.
- When allowed, `StorageStatsManager.QueryStatsForUser(StorageManager.UuidDefault, Process.MyUserHandle())` gives app bytes, data bytes, and cache bytes. `DataBytes` already includes cache, so:
  - Apps = `AppBytes`
  - App data = `DataBytes − CacheBytes`, floored at zero
  - App cache = `CacheBytes`
  - Photos / Videos / Audio = MediaStore `SUM(size)` per collection, only when that collection's permission is fully granted
  - Other = `used − (AppBytes + DataBytes + granted media totals)`, floored at zero
  The visible rows then add up to Used. System-app code inside `AppBytes` can push the sum past Used; the floor hides that instead of showing a negative.
- When not allowed, `Breakdown` is null.
- The provider caches the whole `StorageExtras` payload for 30 s, because `GetAllocatableBytes` is slow and `QueryStatsForUser` walks every package. Used/Free stay live through `DiskInfo` every 2 s. The app-op and media-permission states are checked on every call; a change rebuilds the payload immediately, so granting access does not wait for the cache.
- `IStorageExtrasProvider.OpenUsageAccessSettings()` starts `Settings.ActionUsageAccessSettings` with `ActivityFlags.NewTask`.

### Media permissions

Declared in the manifest:

- `READ_MEDIA_IMAGES`, `READ_MEDIA_VIDEO`, `READ_MEDIA_AUDIO` (Android 13+).
- `READ_EXTERNAL_STORAGE` with `android:maxSdkVersion="32"` (Android 12 and below).

Requested at runtime only when the user taps `Allow media access` on the Storage tab, never at startup. A provider has no activity, so `MainActivity` exposes a `MediaAccessHost.Request` hook in the same style as `OverlayHost.Toggle`; the Android provider's `RequestMediaAccess()` invokes it. The request asks for all three on Android 13+, or `READ_EXTERNAL_STORAGE` below.

Totals come from `MediaStore.Images.Media`, `MediaStore.Video.Media`, and `MediaStore.Audio.Media`, summing `MediaColumns.Size`. On Android 10+ the external-volume content URI is used; below that, `ExternalContentUri`. Pending items are excluded (`IS_PENDING = 0`), and the default query already excludes trashed items.

Honest access rules:

- A category is shown only when its permission is fully granted. On Android 14+ "Select photos" grants `READ_MEDIA_VISUAL_USER_SELECTED`, not `READ_MEDIA_IMAGES`; in that case Photos and Videos hide and their bytes stay in Other. Audio is independent.
- When any category is hidden, `Other` absorbs it, so the rows still add up to Used.
- The `Allow media access` button remains while a category is missing.

Settings' "System" figure still cannot be reproduced; system-app code is inside Apps and any unindexed data lands in Other.

### Storage layout

Inside the existing Internal Storage card, after Free:

- File system, Block size, Reclaimable, Free space change. Each row hidden when null.

After the card:

- `Space usage` card, when Usage Access is granted: Apps, App data, App cache, Photos, Videos, Audio, Other. Media rows hidden until their permission is fully granted.
- When Usage Access is not granted: the same card shows one `mSub` line, `Grant Usage access to see what is using space.`, and a button `Open settings` bound to a new `OpenUsageAccess` command. A small `Button.mAction` style matching the existing dark cards.
- A second `mSub` line and `Allow media access` button while a media category is missing, bound to a new `RequestMediaAccess` command.
- `Other volumes` card: one row per volume with name, usage text, and a progress bar. Hidden when empty.
- One `mSub` note: `Android does not expose device-wide disk I/O to apps.`

`DiskViewModel` gains `FileSystemText`, `BlockSizeText`, `ReclaimableText`, `UsageAccessGranted`, `UsageHintVisible`, `AppsText`, `AppDataText`, `AppCacheText`, `PhotosText`, `VideosText`, `AudioText`, `OtherText`, `MediaHintVisible`, `Volumes` (observable collection of a small row view model), `HasVolumes`, and `FreeSpaceTrendText`. Extras are loaded after specs and refreshed on the existing 2 s tick; the provider does the caching.

## Files touched

- `PerformanceMonitor.Shared/Providers/Abstractions.cs` — `GpuEnvironment`, `IGpuEnvironmentProvider`, `StorageExtras`, `StorageVolumeInfo`, `StorageBreakdown`, `IStorageExtrasProvider`, `DiskInfo.FreeBytes`.
- `PerformanceMonitor.Android/Providers/AndroidProviders.cs` — GPU environment, no temperature fallback, disk list provider, storage extras.
- `PerformanceMonitor.Shared/ViewModels/GpuViewModel.cs` — environment properties.
- `PerformanceMonitor.Shared/ViewModels/DiskViewModel.cs` — extras, trend, volume rows, command.
- `PerformanceMonitor.Shared/Views/MainView.axaml` — GPU and Storage mobile templates, `mAction` style.
- `PerformanceMonitor.Android/MainActivity.cs` — media permission request hook and result handling.
- `PerformanceMonitor.Android/Properties/AndroidManifest.xml` — `PACKAGE_USAGE_STATS`, media read permissions.
- No Android-specific code in shared view models.

## Verification

- Clean Release build: `rm -rf PerformanceMonitor.Android/{obj,bin}` then `dotnet build ... -c Release`; 0 warnings, 0 errors.
- Install with `adb install -r --no-incremental`; relaunch and confirm `logcat -b crash` is empty.
- GPU tab screenshot: GPU name/vendor/APIs/driver, resolution `1080 × 2400`, refresh `60 Hz (max …)`, HDR, wide colour, thermal status. No Temperature row, no battery temperature.
- Storage tab screenshot: live Used/Free, f2fs, block size, reclaimable, trend. Grant `appops set com.performancemonitor.app GET_USAGE_STATS allow`, reopen, confirm Apps/App data/App cache/Other. Revoke it and confirm the hint and button.
- Grant media permissions with `adb shell pm grant` for images, video, and audio; confirm Photos/Videos/Audio rows. Revoke them and confirm the rows hide and `Other` grows. On the phone, tap `Allow media access` once to confirm the runtime dialog appears.
- The phone has no SD card; the other-volumes card is expected to hide. If a removable volume is present, it is checked by hand.

There is no test project in this repo and the work is Android UI/OS integration. Verification is the clean build plus the on-device checks above; pure helpers stay small and are exercised by those screens.

## My calls

- Vulkan is folded into the existing Graphics API row instead of adding a field.
- Battery temperature leaves the GPU tab entirely; it remains on the overlay.
- Media access is opt-in, requested only from the Storage tab, and a partially granted category stays hidden rather than showing a wrong number.
- Two explanatory notes are shown, because otherwise the tabs look broken.
- The whole extras payload is cached for 30 s so the 2 s tick stays cheap; only Used/Free and the trend are live.
- Volumes do not depend on Usage Access.

## Risks

- Some OEMs do not expose a volume list until the card is mounted; the card then hides.
- Thermal status is coarse and can stay at `None` for a long time; it replaces a temperature number, not a temperature graph.
- MediaStore totals cover indexed media only. Files inside `Android/data`, unindexed downloads, and other app-private data stay in Other, so this will not match Settings exactly.
- A partially granted photo/video permission hides those rows; the numbers stay honest but the user may wonder why Photos is missing until they grant full access.
- `QueryStatsForUser` and `GetAllocatableBytes` can take seconds on phones with many packages; the 30 s cache bounds it, and the first read runs on the sampling thread, so the UI is not blocked but the first Storage refresh may arrive late.
- App sizes can exceed Used when system-app code is counted; the floor makes `Other` zero, so the four rows add up to less than Used in that case.
