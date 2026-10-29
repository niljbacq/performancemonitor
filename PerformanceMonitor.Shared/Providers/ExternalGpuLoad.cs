using System;
using System.Collections.Generic;

namespace TaskManager.Providers;

/// <summary>
/// Optional hook so a host app can supply GPU utilization (percent) for adapters
/// the built-in Windows counters could not read. The Desktop app registers a
/// LibreHardwareMonitor-backed reader here; Android never sets it, so the shared
/// project stays free of that dependency.
/// </summary>
public static class ExternalGpuLoad
{
    /// <summary>
    /// Receives the adapters that still have no utilization and fills
    /// <see cref="GpuInfo.Utilization"/> for the ones it can read.
    /// </summary>
    public static Action<IReadOnlyList<GpuInfo>>? Fill { get; set; }
}
