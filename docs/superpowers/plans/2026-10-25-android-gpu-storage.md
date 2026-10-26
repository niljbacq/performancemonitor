# Android GPU and Storage Tabs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the Android GPU and Storage tabs with real sandbox-accessible data: graphics/display/thermal facts, live free space, filesystem and volume details, and a permission-gated app/media space breakdown.

**Architecture:** Android providers implement new read-only interfaces from `PerformanceMonitor.Shared/Providers/Abstractions.cs`. Shared view models consume those interfaces and update the existing mobile templates in `MainView.axaml`. Android APIs stay out of shared code. Missing data stays null and the row hides.

**Tech Stack:** C#, Avalonia, CommunityToolkit.Mvvm, .NET for Android (net10.0-android, min API 24), Android EGL/GLES, DisplayManager, PowerManager, StorageManager, StorageStatsManager, MediaStore, AppOpsManager.

**Spec:** `docs/superpowers/specs/2026-10-25-android-gpu-storage-design.md`

## Global Constraints

- Android target `net10.0-android`, min API 24; shared project `net8.0`.
- No Android namespaces in shared view models. Android work goes through provider interfaces.
- No new NuGet packages. No new source languages.
- No fake values. Null means the row hides.
- Mobile templates only. Desktop views and the overlay are untouched.
- Do not commit or push. The working tree is the deliverable.
- There is no test project in this repo. Each task is verified by a clean Release build plus the on-device check named in the task. The executing-plans ledger records the build command as the task test.
- Build command: `rm -rf PerformanceMonitor.Android/obj PerformanceMonitor.Android/bin && dotnet build PerformanceMonitor.Android/PerformanceMonitor.Android.csproj -c Release`
- Install command: `adb install -r --no-incremental PerformanceMonitor.Android/bin/Release/net10.0-android/com.performancemonitor.app-Signed.apk`
- After every install: `adb shell appops set com.performancemonitor.app SYSTEM_ALERT_WINDOW allow`
- Device: Infinix X6885, 1080 × 2400, Android 16, package `com.performancemonitor.app`, activity `crc640d25df2c30cf8064.MainActivity`.

## Review Focus

- A storage volume whose UUID string is not a valid Java UUID must be skipped, not crash the tab.
- A MediaStore cursor that is null, empty, or loses permission mid-query must hide that row, not crash.
- `GetAllocatableBytes`, `QueryStatsForUser`, or a volume query throwing must keep the previous extras or hide rows, never crash the sampling loop.
- A partial media grant (Android 14+ "Select photos") must hide Photos and Videos and leave their bytes in Other.
- Display, HDR, wide-colour, or thermal APIs unavailable on older Android versions must hide those rows.
- Used smaller than Apps + App data + media must floor Other at zero, never show a negative.

---

### Task 1: GPU tab — driver, Vulkan, display, thermal

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (after `IGpuProvider`)
- Modify: `PerformanceMonitor.Android/Providers/AndroidProviders.cs` (`AndroidGpuProvider`, lines ~474-680)
- Modify: `PerformanceMonitor.Shared/ViewModels/GpuViewModel.cs`
- Modify: `PerformanceMonitor.Shared/Views/MainView.axaml` (GPU mobile template, lines ~1053-1105)

**Interfaces:**
- Consumes: existing `GpuInfoEgl.Result`, `GpuInfo`, `GpuSpecs`, `GpuViewModel` 2 s sample loop.
- Produces:
  - `public sealed class GpuEnvironment` with `string? DisplayResolution`, `RefreshRate`, `Hdr`, `WideColor`, `ThermalStatus`.
  - `public interface IGpuEnvironmentProvider { GpuEnvironment GetEnvironment(); }`
  - `GpuViewModel` properties `DisplayResolution`, `RefreshRateText`, `HdrText`, `WideColorText`, `ThermalStatus`, `HasDisplay`, `HasThermal`.

- [ ] **Step 1: Add the shared contract**

In `Abstractions.cs`, after `IGpuProvider`:

```csharp
public sealed class GpuEnvironment
{
    public string? DisplayResolution { get; set; }
    public string? RefreshRate { get; set; }
    public string? Hdr { get; set; }
    public string? WideColor { get; set; }
    public string? ThermalStatus { get; set; }
}

public interface IGpuEnvironmentProvider
{
    GpuEnvironment GetEnvironment();
}
```

- [ ] **Step 2: Fill the GPU identity rows**

In `AndroidGpuProvider.BuildGpuList()`, replace the `GraphicsApi` assignment and add `DriverVersion`:

```csharp
string? gles = TruncateGlVersion(gl?.Version);
string? vulkan = ReadVulkanVersion();
GraphicsApi = gles == null ? null
    : vulkan == null ? gles
    : $"{gles} · Vulkan {vulkan}";
DriverVersion = DriverBuild(gl?.Version);
```

Add two helpers beside `TruncateGlVersion`:

```csharp
// "OpenGL ES 3.2 v1.r38p1-..." -> "v1.r38p1-..."
private static string? DriverBuild(string? version)
{
    if (string.IsNullOrEmpty(version)) return null;
    int marker = version.IndexOf(" v", StringComparison.Ordinal);
    return marker > 0 ? version[(marker + 1)..].Trim() : null;
}

// android.hardware.vulkan.version packs major/minor/patch as Vulkan does.
private static string? ReadVulkanVersion()
{
    try
    {
        var features = Application.Context.PackageManager?.GetSystemAvailableFeatures();
        if (features == null) return null;
        foreach (var f in features)
        {
            if (f.Name != "android.hardware.vulkan.version" || f.Version == 0) continue;
            int v = f.Version;
            int major = v >> 22;
            int minor = (v >> 12) & 0x3FF;
            int patch = v & 0xFFF;
            return patch > 0 ? $"{major}.{minor}.{patch}" : $"{major}.{minor}";
        }
    }
    catch { }
    return null;
}
```

- [ ] **Step 3: Remove the temperature fallback**

In `AndroidGpuProvider.SampleGpuList()`, delete:

```csharp
string? temp = ReadGpuTemperature() ?? ReadFallbackThermal();
info.TemperatureText = temp;
```

Delete `ReadGpuTemperature()` and `ReadFallbackThermal()`. Leave `TryReadGpuUtilization()` and the `UtilizationText` assignment in place. The Temperature row now hides because `TemperatureText` stays null.

- [ ] **Step 4: Implement `GetEnvironment()`**

Make `AndroidGpuProvider` implement `IGpuEnvironmentProvider`, then add:

```csharp
public GpuEnvironment GetEnvironment()
{
    var env = new GpuEnvironment();
    try
    {
        var dm = Application.Context.GetSystemService(Context.DisplayService) as DisplayManager;
        var display = dm?.GetDisplay(0);
        if (display == null) return env;

        var metrics = new Android.Util.DisplayMetrics();
        display.GetRealMetrics(metrics);
        env.DisplayResolution = $"{metrics.WidthPixels} × {metrics.HeightPixels}";

        int current = (int)Math.Round(display.RefreshRate);
        int max = current;
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            var modes = display.GetSupportedModes();
            if (modes != null && modes.Length > 0)
                max = (int)Math.Round(modes.Max(m => m.RefreshRate));
        }
        env.RefreshRate = max > current ? $"{current} Hz (max {max} Hz)" : $"{current} Hz";

        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            var types = display.HdrCapabilities?.SupportedHdrTypes ?? Array.Empty<HdrType>();
            var names = types.Select(t => t switch
            {
                HdrType.DolbyVision => "Dolby Vision",
                HdrType.Hdr10 => "HDR10",
                HdrType.Hdr10Plus => "HDR10+",
                HdrType.Hlg => "HLG",
                _ => null
            }).Where(n => n != null).ToArray();
            env.Hdr = names.Length == 0 ? "No" : string.Join(", ", names);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            env.WideColor = display.IsWideColorGamut ? "Yes" : "No";

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var power = Application.Context.GetSystemService(Context.PowerService) as PowerManager;
            if (power != null)
            {
                env.ThermalStatus = power.CurrentThermalStatus switch
                {
                    ThermalStatus.None => "None",
                    ThermalStatus.Light => "Light",
                    ThermalStatus.Moderate => "Moderate",
                    ThermalStatus.Severe => "Severe",
                    ThermalStatus.Critical => "Critical",
                    ThermalStatus.Emergency => "Emergency",
                    ThermalStatus.Shutdown => "Shutdown",
                    _ => null
                };
            }
        }
    }
    catch (Exception e)
    {
        Android.Util.Log.Warn("PerfMon", $"GPU environment probe failed: {e.Message}");
    }
    return env;
}
```

Add `using Android.Hardware.Display;`, `using Android.OS;`, and `using Android.Views;` for `DisplayManager`, `PowerManager`/`ThermalStatus`, and `HdrType`/`Display` if the file does not already import them.

- [ ] **Step 5: Wire the view model**

In `GpuViewModel`:
- Add `private readonly IGpuEnvironmentProvider? _environment;` and set it in the constructor from `provider as IGpuEnvironmentProvider`.
- Add properties:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasDisplay))]
private string? _displayResolution;
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasDisplay))]
private string? _refreshRateText;
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasDisplay))]
private string? _hdrText;
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasDisplay))]
private string? _wideColorText;
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasThermal))]
private string? _thermalStatus;

public bool HasDisplay =>
    DisplayResolution != null || RefreshRateText != null || HdrText != null || WideColorText != null;
public bool HasThermal => ThermalStatus != null;
```

- Add `private void ApplyEnvironment(GpuEnvironment env)` assigning all five properties.
- In `LoadGpuSpecsAsync`, after specs are applied, call `ApplyEnvironment(await Task.Run(() => _environment.GetEnvironment()))` when `_environment != null`.
- In the sample loop, when `_listProvider != null`, call `var env = _environment?.GetEnvironment();` before `Dispatcher.UIThread.Post`, and call `ApplyEnvironment(env)` inside the Post when non-null.

- [ ] **Step 6: Add the mobile cards**

In the GPU template, after the Dedicated card:

```xml
<Border Classes="mCard" IsVisible="{Binding HasDisplay}">
    <StackPanel Spacing="6">
        <TextBlock Classes="mCardTitle" Text="Display"/>
        <DockPanel Classes="mRow" IsVisible="{Binding DisplayResolution, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Resolution"/><TextBlock Classes="mValue" Text="{Binding DisplayResolution}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding RefreshRateText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Refresh rate"/><TextBlock Classes="mValue" Text="{Binding RefreshRateText}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding HdrText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="HDR"/><TextBlock Classes="mValue" Text="{Binding HdrText}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding WideColorText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Wide colour"/><TextBlock Classes="mValue" Text="{Binding WideColorText}"/></DockPanel>
    </StackPanel>
</Border>

<Border Classes="mCard" IsVisible="{Binding HasThermal}">
    <StackPanel Spacing="6">
        <TextBlock Classes="mCardTitle" Text="Thermal"/>
        <DockPanel Classes="mRow"><TextBlock Classes="mLabel" Text="Status"/><TextBlock Classes="mValue" Text="{Binding ThermalStatus}"/></DockPanel>
    </StackPanel>
</Border>

<TextBlock Classes="mSub" Text="GPU load and temperature are not exposed by Android."/>
```

- [ ] **Step 7: Build**

Run: `rm -rf PerformanceMonitor.Android/obj PerformanceMonitor.Android/bin && dotnet build PerformanceMonitor.Android/PerformanceMonitor.Android.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Install and verify on the phone**

Run the install command, launch `com.performancemonitor.app/crc640d25df2c30cf8064.MainActivity`, open the GPU tab, screenshot, and run `adb logcat -d -b crash`.

Expected in the screenshot: a GPU name such as `Mali-G57 MC2`; `Graphics API` contains `OpenGL ES` and `Vulkan 1.3`; `Driver version` starts with `v`; `Display` card shows `1080 × 2400`, a refresh rate, HDR, and wide colour; `Thermal` shows a status; no `Temperature` row. Expected in logcat: empty crash buffer.

---

### Task 2: Storage tab — live free space, filesystem, volumes, trend

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (`DiskInfo`, after `IDiskListProvider`)
- Modify: `PerformanceMonitor.Android/Providers/AndroidProviders.cs` (`AndroidDiskProvider`, lines ~406-472)
- Modify: `PerformanceMonitor.Shared/ViewModels/DiskViewModel.cs`
- Modify: `PerformanceMonitor.Shared/Views/MainView.axaml` (Storage mobile template, lines ~910-1000)

**Interfaces:**
- Consumes: `IDiskListProvider`, `DiskInfo`, `DiskSubViewModel`.
- Produces:
  - `DiskInfo.FreeBytes` (`double?`).
  - `public sealed class StorageExtras` with `FileSystem`, `BlockSize`, `Reclaimable`, `IReadOnlyList<StorageVolumeInfo> Volumes`, `StorageBreakdown? Breakdown`.
  - `public sealed class StorageVolumeInfo` with `Name`, `UsageText`, `UsedPercent`.
  - `public sealed class StorageBreakdown` with `Apps`, `AppData`, `AppCache`, `string? Photos`, `string? Videos`, `string? Audio`, `Other`.
  - `public interface IStorageExtrasProvider { StorageExtras GetExtras(); }`
  - `DiskVolumeViewModel` with `Name`, `UsageText`, `UsedPercent`.
  - `DiskViewModel` properties `FileSystemText`, `BlockSizeText`, `ReclaimableText`, `FreeSpaceTrendText`, `Volumes`, `HasVolumes`.

- [ ] **Step 1: Add the shared contract**

Add `public double? FreeBytes { get; set; }` to `DiskInfo`.

After `IDiskListProvider`, add the three classes and the interface from the spec. `StorageBreakdown.Photos/Videos/Audio` stay nullable and unused until Task 4.

- [ ] **Step 2: Make the Android disk provider a list provider**

`AndroidDiskProvider` implements `IDiskProvider, IDiskListProvider, IStorageExtrasProvider`.

Refactor the body of `GetSpecs()` into:

```csharp
private static (double Total, double Free)? ReadStorageNumbers()
{
    // Returns the same total (StorageStatsManager, else StatFs) and free
    // (StatFs AvailableBytes) values the current GetSpecs computes,
    // or null when neither is available.
}

private static DiskInfo BuildDiskInfo()
{
    var d = new DiskInfo { DiskName = "Internal storage" };
    if (ReadStorageNumbers() is not (double total, double free)) return d;

    // existing capacity / used / free / apps-and-data / system-reserved logic,
    // assigning d.Capacity, d.Formatted, d.UsedText, d.FreeText,
    // d.AppsAndDataText, d.SystemReservedText and d.FreeBytes = free.
    return d;
}

public DiskSpecs GetSpecs() { /* map BuildDiskInfo() to DiskSpecs as today */ }
public IReadOnlyList<DiskInfo> GetDiskList() => new[] { BuildDiskInfo() };
public IReadOnlyList<DiskInfo> SampleDiskList() => new[] { BuildDiskInfo() };
```

`Sample()` still returns null; `IDiskProvider` requires it.

- [ ] **Step 3: Implement `GetExtras()`**

Add a 30 s cache:

```csharp
private StorageExtras? _extras;
private DateTime _extrasAtUtc;

public StorageExtras GetExtras()
{
    if (_extras != null && DateTime.UtcNow - _extrasAtUtc < TimeSpan.FromSeconds(30))
        return _extras;
    _extras = BuildExtras();
    _extrasAtUtc = DateTime.UtcNow;
    return _extras;
}
```

`BuildExtras()`: construct `var extras = new StorageExtras();`, fill the file system, block size, reclaimable, and volume sections in their own try/catch blocks, then return `extras`. `Breakdown` stays null in this task.

- File system: read `/proc/mounts` line by line, split on whitespace, find the row whose mount point is `/data`; `FileSystem = $"{fields[2]}{(fields[3].Split(',').Contains("ro") ? " (read-only)" : " (read-write)")}"`. Wrap in try/catch; null on failure.
- Block size: `new StatFs("/data").BlockSizeLong`, then `$"{bytes / 1024} KB"`.
- Reclaimable: on API 26+, `GetAllocatableBytes(UuidDefault) - GetFreeBytes(UuidDefault)`, floored at zero, formatted with `Fmt.Bytes`. Null on throw or below API 26.
- Volumes: iterate `StorageManager.StorageVolumes`, skip `IsPrimary` and volumes whose state is not `Mounted`. For each, try `StorageStatsManager.GetTotalBytes(UUID.FromString(v.GetUuid()))` and `GetFreeBytes(...)`; a `UUID` parse or IO failure skips that volume. When `v.Directory` is available (API 30+), fall back to `StatFs`. Name from `v.GetDescription(Application.Context)`. `UsageText = $"{Fmt.Bytes(used)} of {Fmt.Bytes(total)}"`, `UsedPercent = used / total * 100`. Never throw out of the loop.
- `Breakdown = null` in this task.

- [ ] **Step 4: Extend `DiskViewModel`**

Add:
- `using System.Collections.ObjectModel;` and `using System.Linq;` to `DiskViewModel.cs`.
- `private readonly IStorageExtrasProvider? _extrasProvider;` set in the constructor from `provider as IStorageExtrasProvider`.
- `private StorageExtras? _lastExtras;`.
- Properties `FileSystemText`, `BlockSizeText`, `ReclaimableText`, `FreeSpaceTrendText`, `ObservableCollection<DiskVolumeViewModel> Volumes = new()`, and `HasVolumes => Volumes.Count > 0`.
- `ApplyExtras(StorageExtras extras)`: assign the three strings; if `_lastExtras` is the same reference, stop; otherwise clear and refill `Volumes`, then `OnPropertyChanged(nameof(HasVolumes))`; set `_lastExtras = extras`.
- In `StartMonitoringAsync`'s list-provider branch, after `SampleDiskList()`, call `var extras = _extrasProvider?.GetExtras();` on the background thread and pass it into the existing `Dispatcher.UIThread.Post`, calling `ApplyExtras` when non-null.
- In `ApplySampleToFlat(DiskInfo d)`, after the existing assignments, when `d.FreeBytes is double free`, call a new `TrackFreeSpace(free)`.

Add the trend:

```csharp
private readonly Queue<(DateTime At, double Bytes)> _freeSamples = new();

private void TrackFreeSpace(double free)
{
    var now = DateTime.UtcNow;
    _freeSamples.Enqueue((now, free));
    while (_freeSamples.Count > 0 && now - _freeSamples.Peek().At > TimeSpan.FromMinutes(5))
        _freeSamples.Dequeue();

    if (_freeSamples.Count < 2) { FreeSpaceTrendText = null; return; }

    var first = _freeSamples.Peek();
    var last = _freeSamples.Last();
    var span = last.At - first.At;
    if (span.TotalSeconds < 60) { FreeSpaceTrendText = null; return; }

    double delta = last.Bytes - first.Bytes;
    FreeSpaceTrendText = Math.Abs(delta) < 1024 * 1024
        ? $"No change in {span.TotalMinutes:F0} min"
        : $"{(delta > 0 ? "+" : "-")}{FormatBytes(Math.Abs(delta))} in {span.TotalMinutes:F0} min";
}

internal static string FormatBytes(double bytes)
{
    if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F0} MB";
    return $"{bytes / 1024.0:F0} KB";
}
```

In `DiskSubViewModel.UpdateSample(DiskInfo d)`, copy `UsedText` and `FreeText` when non-null:

```csharp
if (d.UsedText != null) UsedText = d.UsedText;
if (d.FreeText != null) FreeText = d.FreeText;
```

- [ ] **Step 5: Add `DiskVolumeViewModel`**

At the end of `DiskViewModel.cs`:

```csharp
public partial class DiskVolumeViewModel : ViewModelBase
{
    public string Name { get; init; } = "";
    public string UsageText { get; init; } = "";
    public double UsedPercent { get; init; }
}
```

- [ ] **Step 6: Update the Storage template**

Inside the Internal Storage card, after the Free row:

```xml
<DockPanel Classes="mRow" IsVisible="{Binding FileSystemText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="File system"/><TextBlock Classes="mValue" Text="{Binding FileSystemText}"/></DockPanel>
<DockPanel Classes="mRow" IsVisible="{Binding BlockSizeText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Block size"/><TextBlock Classes="mValue" Text="{Binding BlockSizeText}"/></DockPanel>
<DockPanel Classes="mRow" IsVisible="{Binding ReclaimableText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Reclaimable"/><TextBlock Classes="mValue" Text="{Binding ReclaimableText}"/></DockPanel>
<DockPanel Classes="mRow" IsVisible="{Binding FreeSpaceTrendText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Free space change"/><TextBlock Classes="mValue" Text="{Binding FreeSpaceTrendText}"/></DockPanel>
```

After the Internal Storage card, add the volumes card:

```xml
<Border Classes="mCard" IsVisible="{Binding HasVolumes}">
    <StackPanel Spacing="8">
        <TextBlock Classes="mCardTitle" Text="Other volumes"/>
        <ItemsControl ItemsSource="{Binding Volumes}">
            <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:DiskVolumeViewModel">
                    <StackPanel Spacing="4" Margin="0,0,0,8">
                        <DockPanel><TextBlock Classes="mLabel" Text="{Binding Name}"/><TextBlock Classes="mValue" Text="{Binding UsageText}"/></DockPanel>
                        <ProgressBar Minimum="0" Maximum="100" Value="{Binding UsedPercent}" Height="6"/>
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</Border>

<TextBlock Classes="mSub" Text="Android does not expose device-wide disk I/O to apps."/>
```

- [ ] **Step 7: Build**

Run the clean build command.
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Install and verify**

Install, launch, open Storage, wait 6 s, screenshot twice 3 s apart, and check logcat.

Expected: Used and Free change or hold steady between shots; `File system` reads `f2fs (read-write)`; `Block size` reads `4 KB`; `Reclaimable` shows a size; `Free space change` appears after a minute (it may be absent on the first screenshot); `Other volumes` is absent on this phone (no SD card); the disk I/O note is visible. Crash buffer empty.

---

### Task 3: Storage tab — app space usage with Usage Access

**Files:**
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (`IStorageExtrasProvider`)
- Modify: `PerformanceMonitor.Android/Providers/AndroidProviders.cs` (`AndroidDiskProvider`)
- Modify: `PerformanceMonitor.Shared/ViewModels/DiskViewModel.cs`
- Modify: `PerformanceMonitor.Shared/Views/MainView.axaml` (Storage template + mobile styles)
- Modify: `PerformanceMonitor.Android/Properties/AndroidManifest.xml`

**Interfaces:**
- Consumes: `StorageExtras.Breakdown`, `BuildDiskInfo()`, `Fmt.Bytes`.
- Produces: `IStorageExtrasProvider.OpenUsageAccessSettings()`; `DiskViewModel.UsageAccessGranted`, `UsageHintVisible`, `AppsText`, `AppDataText`, `AppCacheText`, `OtherText`, `OpenUsageAccessCommand`.

- [ ] **Step 1: Extend the interface**

```csharp
public interface IStorageExtrasProvider
{
    StorageExtras GetExtras();
    void OpenUsageAccessSettings();
}
```

- [ ] **Step 2: Add the manifest permission**

At the manifest root add `xmlns:tools="http://schemas.android.com/tools"`, and inside `<manifest>`:

```xml
<uses-permission android:name="android.permission.PACKAGE_USAGE_STATS" tools:ignore="ProtectedPermissions" />
```

- [ ] **Step 3: Build the breakdown**

In `BuildExtras()`, before returning `extras`:

```csharp
bool usageGranted = HasUsageAccess();
if (usageGranted && ReadStorageNumbers() is (double total, double free))
{
    var ssm = Application.Context.GetSystemService(Context.StorageStatsService)
              as Android.App.Usage.StorageStatsManager;
    var stats = ssm?.QueryStatsForUser(
        Android.OS.Storage.StorageManager.UuidDefault,
        Android.OS.Process.MyUserHandle());

    if (stats != null)
    {
        double used = total - free;
        long apps = stats.AppBytes;
        long data = Math.Max(0, stats.DataBytes - stats.CacheBytes);
        long cache = stats.CacheBytes;
        long other = Math.Max(0, (long)used - stats.AppBytes - stats.DataBytes);

        extras.Breakdown = new StorageBreakdown
        {
            Apps = Fmt.Bytes(apps),
            AppData = Fmt.Bytes(data),
            AppCache = Fmt.Bytes(cache),
            Other = Fmt.Bytes(other)
        };
    }
}
```

If `ReadStorageNumbers()` is null, leave `Breakdown` null.

Add a NaN-free `used` path instead of the placeholder: compute `used` with the same total and free values used by `BuildDiskInfo()`. If those are unavailable, leave `Breakdown` null.

Permission check:

```csharp
private static bool HasUsageAccess()
{
    try
    {
        var ops = Application.Context.GetSystemService(Context.AppOpsService) as AppOpsManager;
        return ops?.CheckOpNoThrow(
            "android:get_usage_stats",
            Android.OS.Process.MyUid(),
            Application.Context.PackageName!) == AppOpsManagerMode.Allowed;
    }
    catch { return false; }
}
```

Add a permission-state key so granting access invalidates the cache:

```csharp
private bool _lastUsageAccess;
// in GetExtras(): bool now = HasUsageAccess();
// if (now != _lastUsageAccess) { _extras = null; _lastUsageAccess = now; }
```

Place that check before the 30 s cache test.

- [ ] **Step 4: Open Settings from the provider**

```csharp
public void OpenUsageAccessSettings()
{
    try
    {
        var intent = new Intent(Android.Provider.Settings.ActionUsageAccessSettings);
        intent.AddFlags(ActivityFlags.NewTask);
        Application.Context.StartActivity(intent);
    }
    catch (Exception e)
    {
        Android.Util.Log.Warn("PerfMon", $"Could not open usage access settings: {e.Message}");
    }
}
```

- [ ] **Step 5: Extend `DiskViewModel`**

Add `UsageAccessGranted`, `UsageHintVisible => !UsageAccessGranted`, `AppsText`, `AppDataText`, `AppCacheText`, `OtherText`, and:

```csharp
[RelayCommand]
private void OpenUsageAccess() => _extrasProvider?.OpenUsageAccessSettings();
```

In `ApplyExtras`, set `UsageAccessGranted = extras.Breakdown != null`, copy the four strings (null when no breakdown), and call `OnPropertyChanged(nameof(UsageHintVisible))` when the flag changes.

- [ ] **Step 6: Add the card and button style**

In the mobile styles block, before the nav styles:

```xml
<Style Selector="Button.mAction">
    <Setter Property="Background" Value="#3A3A3D"/>
    <Setter Property="Foreground" Value="White"/>
    <Setter Property="CornerRadius" Value="6"/>
    <Setter Property="Padding" Value="12,8"/>
    <Setter Property="HorizontalAlignment" Value="Left"/>
    <Setter Property="Cursor" Value="Hand"/>
</Style>
<Style Selector="Button.mAction:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="#444448"/>
</Style>
```

After the volumes card:

```xml
<Border Classes="mCard">
    <StackPanel Spacing="6">
        <TextBlock Classes="mCardTitle" Text="Space usage"/>
        <TextBlock Classes="mSub" IsVisible="{Binding UsageHintVisible}" Text="Grant Usage access to see what is using space."/>
        <Button Classes="mAction" IsVisible="{Binding UsageHintVisible}" Content="Open settings" Command="{Binding OpenUsageAccessCommand}"/>
        <DockPanel Classes="mRow" IsVisible="{Binding AppsText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Apps"/><TextBlock Classes="mValue" Text="{Binding AppsText}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding AppDataText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="App data"/><TextBlock Classes="mValue" Text="{Binding AppDataText}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding AppCacheText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="App cache"/><TextBlock Classes="mValue" Text="{Binding AppCacheText}"/></DockPanel>
        <DockPanel Classes="mRow" IsVisible="{Binding OtherText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Other"/><TextBlock Classes="mValue" Text="{Binding OtherText}"/></DockPanel>
    </StackPanel>
</Border>
```

- [ ] **Step 7: Build**

Run the clean build command.
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Install and verify both permission states**

1. Install, launch, open Storage. Expected: `Grant Usage access to see what is using space.` and `Open settings`.
2. Run `adb shell appops set com.performancemonitor.app GET_USAGE_STATS allow`, force-stop, relaunch, open Storage, wait 5 s. Expected: Apps / App data / App cache / Other rows appear and are not negative.
3. Run `adb shell appops set com.performancemonitor.app GET_USAGE_STATS deny`, relaunch. Expected: the hint returns.
4. Check `adb logcat -d -b crash`: empty.

---

### Task 4: Storage tab — Photos, Videos, Audio split

**Files:**
- Modify: `PerformanceMonitor.Android/Properties/AndroidManifest.xml`
- Create: `PerformanceMonitor.Android/MediaAccessHost.cs`
- Modify: `PerformanceMonitor.Android/MainActivity.cs`
- Modify: `PerformanceMonitor.Android/Providers/AndroidProviders.cs`
- Modify: `PerformanceMonitor.Shared/Providers/Abstractions.cs` (`IStorageExtrasProvider`)
- Modify: `PerformanceMonitor.Shared/ViewModels/DiskViewModel.cs`
- Modify: `PerformanceMonitor.Shared/Views/MainView.axaml`

**Interfaces:**
- Consumes: `StorageBreakdown.Photos/Videos/Audio`, `DiskViewModel` space usage card.
- Produces: `IStorageExtrasProvider.RequestMediaAccess()`; `DiskViewModel.PhotosText`, `VideosText`, `AudioText`, `MediaHintVisible`, `RequestMediaAccessCommand`.

- [ ] **Step 1: Add media permissions**

Inside `<manifest>`:

```xml
<uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
<uses-permission android:name="android.permission.READ_MEDIA_VIDEO" />
<uses-permission android:name="android.permission.READ_MEDIA_AUDIO" />
<uses-permission android:name="android.permission.READ_MEDIA_VISUAL_USER_SELECTED" />
<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" android:maxSdkVersion="32" />
```

- [ ] **Step 2: Add the activity hook**

Create `MediaAccessHost.cs`:

```csharp
namespace TaskManager.Droid;

internal static class MediaAccessHost
{
    public static Action? Request { get; set; }
}
```

Add `using System;`.

In `MainActivity.OnCreate`, after `OverlayHost.Toggle = ...`:

```csharp
MediaAccessHost.Request = RequestMediaPermissions;
```

Add:

```csharp
private const int MediaPermissionRequestCode = 1002;

private void RequestMediaPermissions()
{
    var needed = new List<string>();
    if (OperatingSystem.IsAndroidVersionAtLeast(33))
    {
        if (CheckSelfPermission(Android.Manifest.Permission.ReadMediaImages) != Permission.Granted)
            needed.Add(Android.Manifest.Permission.ReadMediaImages);
        if (CheckSelfPermission(Android.Manifest.Permission.ReadMediaVideo) != Permission.Granted)
            needed.Add(Android.Manifest.Permission.ReadMediaVideo);
        if (CheckSelfPermission(Android.Manifest.Permission.ReadMediaAudio) != Permission.Granted)
            needed.Add(Android.Manifest.Permission.ReadMediaAudio);
    }
    else if (CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) != Permission.Granted)
    {
        needed.Add(Android.Manifest.Permission.ReadExternalStorage);
    }

    if (needed.Count > 0)
        RequestPermissions(needed.ToArray(), MediaPermissionRequestCode);
}
```

Clear the hook in `OnDestroy`:

```csharp
protected override void OnDestroy()
{
    MediaAccessHost.Request = null;
    base.OnDestroy();
}
```

- [ ] **Step 3: Implement media totals**

Add to `IStorageExtrasProvider`:

```csharp
void RequestMediaAccess();
```

In the Android provider:

```csharp
public void RequestMediaAccess() => MediaAccessHost.Request?.Invoke();
```

Permission state:

```csharp
private static (bool Images, bool Videos, bool Audio) MediaAccess()
{
    if (OperatingSystem.IsAndroidVersionAtLeast(33))
        return (
            Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaImages) == Permission.Granted,
            Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaVideo) == Permission.Granted,
            Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadMediaAudio) == Permission.Granted);

    bool external = Application.Context.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted;
    return (external, external, external);
}
```

Totals:

```csharp
private static long SumMedia(Android.Net.Uri uri)
{
    try
    {
        string column = $"SUM({MediaStore.MediaColumns.Size})";
        string? selection = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? $"{MediaStore.MediaColumns.IsPending} = 0" : null;
        using var cursor = Application.Context.ContentResolver.Query(uri, new[] { column }, selection, null, null);
        return cursor != null && cursor.MoveToFirst() ? cursor.GetLong(0) : 0;
    }
    catch { return 0; }
}

private static Android.Net.Uri MediaUri(string kind)
{
    bool externalVolume = OperatingSystem.IsAndroidVersionAtLeast(29);
    return kind switch
    {
        "images" => externalVolume ? MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternal) : MediaStore.Images.Media.ExternalContentUri,
        "videos" => externalVolume ? MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternal) : MediaStore.Video.Media.ExternalContentUri,
        _ => externalVolume ? MediaStore.Audio.Media.GetContentUri(MediaStore.VolumeExternal) : MediaStore.Audio.Media.ExternalContentUri
    };
}
```

In `BuildExtras()`, when the breakdown is built:

```csharp
var media = MediaAccess();
long photos = media.Images ? SumMedia(MediaUri("images")) : 0;
long videos = media.Videos ? SumMedia(MediaUri("videos")) : 0;
long audio = media.Audio ? SumMedia(MediaUri("audio")) : 0;

extras.Breakdown.Photos = media.Images ? Fmt.Bytes(photos) : null;
extras.Breakdown.Videos = media.Videos ? Fmt.Bytes(videos) : null;
extras.Breakdown.Audio = media.Audio ? Fmt.Bytes(audio) : null;
extras.Breakdown.Other = Fmt.Bytes(Math.Max(0, (long)used - stats.AppBytes - stats.DataBytes - photos - videos - audio));
```

Invalidate the cache when any of the three permission bits changes, in the same way as the Usage Access key.

- [ ] **Step 4: Extend `DiskViewModel`**

Add `PhotosText`, `VideosText`, `AudioText`, and:

```csharp
public bool MediaHintVisible => UsageAccessGranted &&
    (PhotosText == null || VideosText == null || AudioText == null);

[RelayCommand]
private void RequestMediaAccess() => _extrasProvider?.RequestMediaAccess();
```

In `ApplyExtras`, copy the three strings and call `OnPropertyChanged(nameof(MediaHintVisible))` after `UsageAccessGranted` and the media fields change.

- [ ] **Step 5: Add media rows and button**

In the Space usage card, after the Other row:

```xml
<DockPanel Classes="mRow" IsVisible="{Binding PhotosText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Photos"/><TextBlock Classes="mValue" Text="{Binding PhotosText}"/></DockPanel>
<DockPanel Classes="mRow" IsVisible="{Binding VideosText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Videos"/><TextBlock Classes="mValue" Text="{Binding VideosText}"/></DockPanel>
<DockPanel Classes="mRow" IsVisible="{Binding AudioText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"><TextBlock Classes="mLabel" Text="Audio"/><TextBlock Classes="mValue" Text="{Binding AudioText}"/></DockPanel>
<TextBlock Classes="mSub" IsVisible="{Binding MediaHintVisible}" Text="Allow media access to split photos, videos, and audio out of Other."/>
<Button Classes="mAction" IsVisible="{Binding MediaHintVisible}" Content="Allow media access" Command="{Binding RequestMediaAccessCommand}"/>
```

- [ ] **Step 6: Build**

Run the clean build command.
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Install and verify**

1. Install, launch, open Storage. Expected: media hint and `Allow media access` button; Photos/Videos/Audio rows absent.
2. Grant with:

```bash
adb shell pm grant com.performancemonitor.app android.permission.READ_MEDIA_IMAGES
adb shell pm grant com.performancemonitor.app android.permission.READ_MEDIA_VIDEO
adb shell pm grant com.performancemonitor.app android.permission.READ_MEDIA_AUDIO
```

Force-stop, relaunch, open Storage, wait 5 s. Expected: Photos, Videos, Audio rows appear; Other shrinks; nothing negative.
3. Revoke the three grants, relaunch. Expected: media rows hide, Other grows, hint returns.
4. Tap `Allow media access` once. Expected: the Android permission dialog appears (screenshot it), then rows update after granting or stay hidden after denying.
5. If the dialog offers `Select photos` on this Android version, choose it and confirm Photos and Videos hide while Audio still shows if granted. This is the partial-access rule.
6. `adb logcat -d -b crash`: empty.

---

### Task 5: Full regression

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Clean-build Android**

Run the clean build command.
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Install and run the full tab pass**

Install; launch; visit Home, CPU, Memory, Storage, Wi-Fi, GPU; open the overlay; wait 10 s.
Expected: no crash, Storage and GPU show the new rows, existing tabs unchanged.

- [ ] **Step 3: Check the crash buffer**

Run: `adb logcat -d -b crash`
Expected: no entries for `com.performancemonitor.app`.

- [ ] **Step 4: Build and launch Desktop**

Run: `dotnet build PerformanceMonitor.Desktop/PerformanceMonitor.Desktop.csproj -c Release`
Expected: `0 Warning(s) 0 Error(s)`.

Run: `DISPLAY=:0 ./PerformanceMonitor.Desktop/bin/Release/net8.0/PerformanceMonitor.Desktop`
Expected: window opens with existing pages; new Android-only rows are absent because the desktop providers do not implement the new interfaces.

- [ ] **Step 5: Report**

Record screenshots and the build results for the final message. Do not commit.
