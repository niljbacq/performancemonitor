using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class DiskViewModel : ViewModelBase
{
    private string _diskName = "Disk";
    public string DiskName { get => _diskName; set => SetProperty(ref _diskName, value); }

    private string _modelName = "Detecting Disk...";
    public string ModelName { get => _modelName; set => SetProperty(ref _modelName, value); }

    private string? _activeTime;
    public string? ActiveTime { get => _activeTime; set => SetProperty(ref _activeTime, value); }

    private double _activeTimeValue = 0;
    public double ActiveTimeValue { get => _activeTimeValue; set => SetProperty(ref _activeTimeValue, value); }

    private string? _averageResponseTime;
    public string? AverageResponseTime { get => _averageResponseTime; set => SetProperty(ref _averageResponseTime, value); }

    private string? _readSpeed;
    public string? ReadSpeed { get => _readSpeed; set => SetProperty(ref _readSpeed, value); }

    private string? _writeSpeed;
    public string? WriteSpeed { get => _writeSpeed; set => SetProperty(ref _writeSpeed, value); }

    private string _capacity = "-";
    public string Capacity { get => _capacity; set => SetProperty(ref _capacity, value); }

    private string _formatted = "-";
    public string Formatted { get => _formatted; set => SetProperty(ref _formatted, value); }

    private string _systemDisk = "No";
    public string SystemDisk { get => _systemDisk; set => SetProperty(ref _systemDisk, value); }

    private string _pageFile = "No";
    public string PageFile { get => _pageFile; set => SetProperty(ref _pageFile, value); }

    private string _type = "Unknown";
    public string Type { get => _type; set => SetProperty(ref _type, value); }

    [ObservableProperty] private string? _usedText;
    [ObservableProperty] private string? _freeText;
    [ObservableProperty] private string? _appsAndDataText;
    [ObservableProperty] private string? _systemReservedText;
    [ObservableProperty] private string? _ioPressureText;

    public DiskSubViewModel Ssd { get; } = new DiskSubViewModel("SSD", "No SSD detected");
    public DiskSubViewModel Hdd { get; } = new DiskSubViewModel("HDD", "No HDD detected");
    public DiskSubViewModel Other { get; } = new DiskSubViewModel("Storage", "No storage detected");

    private readonly IDiskProvider _provider;
    private readonly IDiskListProvider? _listProvider;
    private CancellationTokenSource? _cts;
    private bool _specsLoaded;

    public DiskViewModel() : this(ProviderFactory.CreateDisk()) { }

    public DiskViewModel(IDiskProvider provider)
    {
        _provider = provider;
        _listProvider = provider as IDiskListProvider;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        if (!_specsLoaded)
        {
            _specsLoaded = true;
            _ = LoadDiskSpecsAsync();
        }

        _ = StartMonitoringAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoadDiskSpecsAsync()
    {
        if (_listProvider != null)
        {
            var list = await Task.Run(() => _listProvider.GetDiskList());
            ApplySpecs(list);
        }
        else
        {
            var s = await Task.Run(() => _provider.GetSpecs());
            ApplySingleSpec(s);
        }
    }

    private void ApplySingleSpec(DiskSpecs s)
    {
        if (s.DiskName != null) DiskName = s.DiskName;
        if (s.ModelName != null) ModelName = s.ModelName;
        if (s.Capacity != null) Capacity = s.Capacity;
        if (s.Formatted != null) Formatted = s.Formatted;
        if (s.SystemDisk != null) SystemDisk = s.SystemDisk;
        if (s.PageFile != null) PageFile = s.PageFile;
        if (s.Type != null) Type = s.Type;
        UsedText = s.UsedText;
        FreeText = s.FreeText;
        AppsAndDataText = s.AppsAndDataText;
        SystemReservedText = s.SystemReservedText;
        IoPressureText = s.PressureText;

        var info = new DiskInfo
        {
            DiskName = s.DiskName,
            ModelName = s.ModelName,
            Type = s.Type,
            Capacity = s.Capacity,
            Formatted = s.Formatted,
            SystemDisk = s.SystemDisk,
            PageFile = s.PageFile,
            UsedText = s.UsedText,
            FreeText = s.FreeText,
            AppsAndDataText = s.AppsAndDataText,
            SystemReservedText = s.SystemReservedText
        };

        if (IsHdd(info))
        {
            Hdd.Update(info);
            Ssd.ResetToEmpty();
            Other.ResetToEmpty();
        }
        else if (IsSsd(info))
        {
            Ssd.Update(info);
            Hdd.ResetToEmpty();
            Other.ResetToEmpty();
        }
        else
        {
            // Unknown type: show it neutrally instead of pretending it is an SSD.
            Other.Update(info);
            Ssd.ResetToEmpty();
            Hdd.ResetToEmpty();
        }
    }

    private void ApplySpecs(IReadOnlyList<DiskInfo> list)
    {
        var ssd = PickPrimary(list, IsSsd);
        var hdd = PickPrimary(list, IsHdd);
        var other = PickPrimary(list, d => !IsSsd(d) && !IsHdd(d));

        if (ssd != null) Ssd.Update(ssd); else Ssd.ResetToEmpty();
        if (hdd != null) Hdd.Update(hdd); else Hdd.ResetToEmpty();
        if (other != null) Other.Update(other); else Other.ResetToEmpty();

        var primary = ssd ?? hdd ?? other;
        if (primary != null)
        {
            DiskName = primary.DiskName ?? "Disk";
            ModelName = primary.ModelName ?? "Generic Storage Device";
            Capacity = primary.Capacity ?? "-";
            Formatted = primary.Formatted ?? "-";
            SystemDisk = primary.SystemDisk ?? "No";
            PageFile = primary.PageFile ?? "No";
            Type = primary.Type ?? "Unknown";
            UsedText = primary.UsedText;
            FreeText = primary.FreeText;
            AppsAndDataText = primary.AppsAndDataText;
            SystemReservedText = primary.SystemReservedText;
            IoPressureText = primary.PressureText;
        }
    }

    private async Task StartMonitoringAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (_listProvider != null)
                    {
                        var samples = _listProvider.SampleDiskList();
                        var ssd = PickPrimary(samples, IsSsd);
                        var hdd = PickPrimary(samples, IsHdd);
                        var other = PickPrimary(samples, d => !IsSsd(d) && !IsHdd(d));

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ssd != null) Ssd.UpdateSample(ssd); else Ssd.ResetToEmpty();
                            if (hdd != null) Hdd.UpdateSample(hdd); else Hdd.ResetToEmpty();
                            if (other != null) Other.UpdateSample(other); else Other.ResetToEmpty();

                            var primary = ssd ?? hdd ?? other;
                            if (primary != null) ApplySampleToFlat(primary);
                            else ClearFlatSample();
                        });
                    }
                    else
                    {
                        var sample = _provider.Sample();

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (sample == null || sample.NotSupported)
                            {
                                ClearFlatSample();
                                if (Hdd.HasDisk) Hdd.UpdateSample(new DiskSample { NotSupported = true });
                                else if (Ssd.HasDisk) Ssd.UpdateSample(new DiskSample { NotSupported = true });
                                else if (Other.HasDisk) Other.UpdateSample(new DiskSample { NotSupported = true });
                                return;
                            }

                            ApplySampleToFlat(sample);

                            if (Hdd.HasDisk) Hdd.UpdateSample(sample);
                            else if (Ssd.HasDisk) Ssd.UpdateSample(sample);
                            else if (Other.HasDisk) Other.UpdateSample(sample);
                        });
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ClearFlatSample()
    {
        ReadSpeed = null;
        WriteSpeed = null;
        ActiveTime = null;
        ActiveTimeValue = 0;
        AverageResponseTime = null;
    }

    private void ApplySampleToFlat(DiskSample d)
    {
        if (d.NotSupported)
        {
            ClearFlatSample();
            return;
        }

        ReadSpeed = d.ReadBytesPerSec is double r ? FormatSpeed(r) : null;
        WriteSpeed = d.WriteBytesPerSec is double w ? FormatSpeed(w) : null;
        ActiveTimeValue = d.ActivePercent ?? 0;
        ActiveTime = d.ActivePercent is double a ? $"{a}%" : null;
        AverageResponseTime = d.ResponseMs is double ms ? $"{ms:F1} ms" : null;
    }

    private void ApplySampleToFlat(DiskInfo d)
    {
        if (d.NotSupported)
        {
            ClearFlatSample();
            return;
        }

        ReadSpeed = d.ReadBytesPerSec is double r ? FormatSpeed(r) : null;
        WriteSpeed = d.WriteBytesPerSec is double w ? FormatSpeed(w) : null;
        ActiveTimeValue = d.ActivePercent ?? 0;
        ActiveTime = d.ActivePercent is double a ? $"{a}%" : null;
        AverageResponseTime = d.ResponseMs is double ms ? $"{ms:F1} ms" : null;
    }

    private static bool IsSsd(DiskInfo d)
        => d.Type == "SSD" || d.Type == "NVMe SSD";

    private static bool IsHdd(DiskInfo d)
        => d.Type == "HDD";

    private static DiskInfo? PickPrimary(IReadOnlyList<DiskInfo> list, Func<DiskInfo, bool> match)
    {
        DiskInfo? best = null;
        foreach (var d in list)
        {
            if (!match(d)) continue;
            if (best == null) { best = d; continue; }
            if (d.SystemDisk == "Yes" && best.SystemDisk != "Yes") best = d;
        }
        return best;
    }

    internal static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{Math.Round(bytesPerSec / (1024.0 * 1024.0), 1)} MB/s";
        return $"{Math.Round(bytesPerSec / 1024.0, 0)} KB/s";
    }
}

public partial class DiskSubViewModel : ViewModelBase
{
    public string EmptyLabel { get; }
    public string TypeLabel { get; }

    private bool _hasDisk;
    public bool HasDisk { get => _hasDisk; set => SetProperty(ref _hasDisk, value); }

    private string _diskName = "";
    public string DiskName { get => _diskName; set => SetProperty(ref _diskName, value); }

    private string _modelName = "";
    public string ModelName { get => _modelName; set => SetProperty(ref _modelName, value); }

    private string? _activeTime;
    public string? ActiveTime { get => _activeTime; set => SetProperty(ref _activeTime, value); }

    private double _activeTimeValue;
    public double ActiveTimeValue { get => _activeTimeValue; set => SetProperty(ref _activeTimeValue, value); }

    private string? _averageResponseTime;
    public string? AverageResponseTime { get => _averageResponseTime; set => SetProperty(ref _averageResponseTime, value); }

    private string? _readSpeed;
    public string? ReadSpeed { get => _readSpeed; set => SetProperty(ref _readSpeed, value); }

    // Raw bytes/sec for the mobile read/write speed bars.
    private double? _readSpeedBytes;
    public double? ReadSpeedBytes { get => _readSpeedBytes; set => SetProperty(ref _readSpeedBytes, value); }

    private string? _writeSpeed;
    public string? WriteSpeed { get => _writeSpeed; set => SetProperty(ref _writeSpeed, value); }

    private double? _writeSpeedBytes;
    public double? WriteSpeedBytes { get => _writeSpeedBytes; set => SetProperty(ref _writeSpeedBytes, value); }

    private string _capacity = "-";
    public string Capacity { get => _capacity; set => SetProperty(ref _capacity, value); }

    private string _formatted = "-";
    public string Formatted { get => _formatted; set => SetProperty(ref _formatted, value); }

    private string? _systemDisk;
    public string? SystemDisk { get => _systemDisk; set => SetProperty(ref _systemDisk, value); }

    private string? _pageFile;
    public string? PageFile { get => _pageFile; set => SetProperty(ref _pageFile, value); }

    private string? _usedText;
    public string? UsedText { get => _usedText; set => SetProperty(ref _usedText, value); }

    private string? _freeText;
    public string? FreeText { get => _freeText; set => SetProperty(ref _freeText, value); }

    private string? _appsAndDataText;
    public string? AppsAndDataText { get => _appsAndDataText; set => SetProperty(ref _appsAndDataText, value); }

    private string? _systemReservedText;
    public string? SystemReservedText { get => _systemReservedText; set => SetProperty(ref _systemReservedText, value); }

    public DiskSubViewModel(string typeLabel, string emptyLabel)
    {
        TypeLabel = typeLabel;
        EmptyLabel = emptyLabel;
    }

    public void Update(DiskInfo d)
    {
        HasDisk = true;
        DiskName = d.DiskName ?? "Disk";
        ModelName = d.ModelName ?? "Generic Storage Device";
        Capacity = d.Capacity ?? "-";
        Formatted = d.Formatted ?? "-";
        SystemDisk = d.SystemDisk;
        PageFile = d.PageFile;
        UsedText = d.UsedText;
        FreeText = d.FreeText;
        AppsAndDataText = d.AppsAndDataText;
        SystemReservedText = d.SystemReservedText;
    }

    public void UpdateSample(DiskInfo d)
    {
        if (d.NotSupported)
        {
            ReadSpeed = null;
            ReadSpeedBytes = null;
            WriteSpeed = null;
            WriteSpeedBytes = null;
            ActiveTime = null;
            ActiveTimeValue = 0;
            AverageResponseTime = null;
            return;
        }

        ReadSpeedBytes = d.ReadBytesPerSec;
        ReadSpeed = d.ReadBytesPerSec is double r ? DiskViewModel.FormatSpeed(r) : null;
        WriteSpeedBytes = d.WriteBytesPerSec;
        WriteSpeed = d.WriteBytesPerSec is double w ? DiskViewModel.FormatSpeed(w) : null;
        ActiveTimeValue = d.ActivePercent ?? 0;
        ActiveTime = d.ActivePercent is double a ? $"{a}%" : null;
        AverageResponseTime = d.ResponseMs is double ms ? $"{ms:F1} ms" : null;
    }

    public void UpdateSample(DiskSample d)
    {
        if (d.NotSupported)
        {
            ReadSpeed = null;
            ReadSpeedBytes = null;
            WriteSpeed = null;
            WriteSpeedBytes = null;
            ActiveTime = null;
            ActiveTimeValue = 0;
            AverageResponseTime = null;
            return;
        }

        ReadSpeedBytes = d.ReadBytesPerSec;
        ReadSpeed = d.ReadBytesPerSec is double r ? DiskViewModel.FormatSpeed(r) : null;
        WriteSpeedBytes = d.WriteBytesPerSec;
        WriteSpeed = d.WriteBytesPerSec is double w ? DiskViewModel.FormatSpeed(w) : null;
        ActiveTimeValue = d.ActivePercent ?? 0;
        ActiveTime = d.ActivePercent is double a ? $"{a}%" : null;
        AverageResponseTime = d.ResponseMs is double ms ? $"{ms:F1} ms" : null;
    }

    public void ResetToEmpty()
    {
        HasDisk = false;
        DiskName = "";
        ModelName = EmptyLabel;
        Capacity = "-";
        Formatted = "-";
        SystemDisk = null;
        PageFile = null;
        UsedText = null;
        FreeText = null;
        AppsAndDataText = null;
        SystemReservedText = null;
        ActiveTime = null;
        ActiveTimeValue = 0;
        AverageResponseTime = null;
        ReadSpeed = null;
        ReadSpeedBytes = null;
        WriteSpeed = null;
        WriteSpeedBytes = null;
    }
}
