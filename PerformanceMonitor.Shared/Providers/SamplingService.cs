using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.Providers;

public sealed record PerfSample(
    DateTime Utc,
    float CpuPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double MemoryPercent,
    double? DiskReadBps,
    double? DiskWriteBps,
    double? DiskActivePercent,
    double? GpuUtilization,
    double? GpuTemperatureC);

/// <summary>
/// Owns a single background sampling loop that reads every provider on a fixed
/// interval, publishes immutable <see cref="PerfSample"/> values and appends
/// them to a CSV file. The writer flushes every few seconds and uses UTC
/// ISO-8601 timestamps and invariant formatting; missing values become empty
/// cells (the CSV form of "hidden row").
/// </summary>
public sealed class SamplingService : IDisposable
{
    public static SamplingService? Current { get; private set; }

    private const string Header =
        "timestamp_utc,cpu_percent,memory_used_gb,memory_total_gb,memory_percent," +
        "disk_read_bps,disk_write_bps,disk_active_percent,gpu_utilization_percent,gpu_temperature_c";

    private readonly ICpuProvider _cpu;
    private readonly IMemoryProvider _memory;
    private readonly IDiskProvider _disk;
    private readonly IGpuProvider _gpu;

    private readonly TimeSpan _interval;
    private readonly int _flushEverySamples;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamWriter? _writer;

    public string? LogPath { get; private set; }

    /// <summary>Raised on the sampling thread after each sample is collected.</summary>
    public event Action<PerfSample>? SampleCollected;

    public SamplingService(
        ICpuProvider? cpu = null,
        IMemoryProvider? memory = null,
        IDiskProvider? disk = null,
        IGpuProvider? gpu = null,
        TimeSpan? interval = null)
    {
        _cpu = cpu ?? ProviderFactory.CreateCpu();
        _memory = memory ?? ProviderFactory.CreateMemory();
        _disk = disk ?? ProviderFactory.CreateDisk();
        _gpu = gpu ?? ProviderFactory.CreateGpu();
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _flushEverySamples = Math.Max(1, (int)(5 / Math.Max(1, _interval.TotalSeconds)));
    }

    /// <summary>Starts the process-wide service once (no-op if already running).</summary>
    public static SamplingService StartDefault()
    {
        if (Current != null) return Current;
        var service = new SamplingService();
        service.Start();
        Current = service;
        return service;
    }

    public void Start()
    {
        if (_loop != null) return;
        try
        {
            Directory.CreateDirectory(ProviderFactory.LogDirectory());
            string file = $"perfmon-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
            LogPath = Path.Combine(ProviderFactory.LogDirectory(), file);
            _writer = new StreamWriter(LogPath, append: false) { AutoFlush = false };
            _writer.WriteLine(Header);
            _writer.Flush();
        }
        catch
        {
            _writer = null;   // logging is best-effort; still publish samples
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
        _loop = null;
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
        if (Current == this) Current = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        int sinceFlush = 0;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            PerfSample sample;
            try { sample = Collect(); }
            catch { continue; }

            try { SampleCollected?.Invoke(sample); } catch { }

            if (_writer != null)
            {
                try
                {
                    _writer.WriteLine(Format(sample));
                    if (++sinceFlush >= _flushEverySamples)
                    {
                        _writer.Flush();
                        sinceFlush = 0;
                    }
                }
                catch { }
            }
        }

        try { _writer?.Flush(); } catch { }
    }

    private PerfSample Collect()
    {
        DateTime utc = DateTime.UtcNow;

        float cpu = 0;
        try { cpu = _cpu.GetUsagePercent(); } catch { }

        double usedGb = 0, totalGb = 0, memPercent = 0;
        try
        {
            var m = _memory.GetUsage();
            usedGb = m.UsedGB;
            totalGb = m.TotalGB;
            memPercent = m.Percent;
        }
        catch { }

        double? read = null, write = null, active = null;
        try
        {
            var d = _disk.Sample();
            if (d != null && !d.NotSupported)
            {
                read = d.ReadBytesPerSec;
                write = d.WriteBytesPerSec;
                active = d.ActivePercent;
            }
        }
        catch { }

        double? gpuUtil = null, gpuTemp = null;
        try
        {
            var g = _gpu.Sample();
            if (g != null)
            {
                gpuUtil = g.Utilization;
                gpuTemp = g.TemperatureC;
            }
        }
        catch { }

        return new PerfSample(utc, cpu, usedGb, totalGb, memPercent, read, write, active, gpuUtil, gpuTemp);
    }

    private static string Format(PerfSample s)
    {
        var c = CultureInfo.InvariantCulture;
        return string.Join(',',
            s.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", c),
            s.CpuPercent.ToString("F1", c),
            s.MemoryUsedGb.ToString("F2", c),
            s.MemoryTotalGb.ToString("F2", c),
            s.MemoryPercent.ToString("F0", c),
            Num(s.DiskReadBps),
            Num(s.DiskWriteBps),
            Num(s.DiskActivePercent),
            Num(s.GpuUtilization),
            Num(s.GpuTemperatureC));
    }

    private static string Num(double? value)
        => value is double v ? v.ToString("F2", CultureInfo.InvariantCulture) : "";
}
