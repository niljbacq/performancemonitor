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
    // ---- Flat properties (kept for compatibility with any remaining bindings) ----
    private string _diskName = "Disk";
    public string DiskName { get => _diskName; set => SetProperty(ref _diskName, value); }

    private string _modelName = "Detecting Disk...";
    public string ModelName { get => _modelName; set => SetProperty(ref _modelName, value); }

    private string _activeTime = "0%";
    public string ActiveTime { get => _activeTime; set => SetProperty(ref _activeTime, value); }

    private double _activeTimeValue = 0;
    public double ActiveTimeValue { get => _activeTimeValue; set => SetProperty(ref _activeTimeValue, value); }

    private string _averageResponseTime = "0.0 ms";
    public string AverageResponseTime { get => _averageResponseTime; set => SetProperty(ref _averageResponseTime, value); }

    private string _readSpeed = "0 KB/s";
    public string ReadSpeed { get => _readSpeed; set => SetProperty(ref _readSpeed, value); }

    private string _writeSpeed = "0 KB/s";
    public string WriteSpeed { get => _writeSpeed; set => SetProperty(ref _writeSpeed, value); }

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

    // ---- SSD / HDD split ----
    public DiskSubViewModel Ssd { get; } = new DiskSubViewModel("SSD", "No SSD detected");
    public DiskSubViewModel Hdd { get; } = new DiskSubViewModel("HDD", "No HDD detected");

    private readonly IDiskProvider _provider;
    private readonly IDiskListProvider? _listProvider;

    public DiskViewModel() : this(ProviderFactory.CreateDisk()) { }

    public DiskViewModel(IDiskProvider provider)
    {
        _provider = provider;
        _listProvider = provider as IDiskListProvider;
        _ = LoadDiskSpecsAsync();
        _ = StartMonitoringAsync();
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
        }
        else
        {
            Ssd.Update(info);
            Hdd.ResetToEmpty();
        }
    }

    private void ApplySpecs(IReadOnlyList<DiskInfo> list)
    {
        var ssd = PickPrimary(list, IsSsd);
        var hdd = PickPrimary(list, IsHdd);

        if (ssd != null) Ssd.Update(ssd); else Ssd.ResetToEmpty();
        if (hdd != null) Hdd.Update(hdd); else Hdd.ResetToEmpty();

        var primary = ssd ?? hdd;
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
        }
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                if (_listProvider != null)
                {
                    var samples = _listProvider.SampleDiskList();
                    var ssd = PickPrimary(samples, IsSsd);
                    var hdd = PickPrimary(samples, IsHdd);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ssd != null) Ssd.UpdateSample(ssd);
                        if (hdd != null) Hdd.UpdateSample(hdd);

                        var primary = ssd ?? hdd;
                        if (primary != null) ApplySampleToFlat(primary);
                    });
                }
                else
                {
                    var sample = _provider.Sample();
                    if (sample == null) continue;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (sample.NotSupported)
                        {
                            string msg = sample.NotSupportedMessage ?? "N/A";
                            ReadSpeed = msg;
                            WriteSpeed = msg;
                            ActiveTime = msg;
                            AverageResponseTime = msg;

                            if (Hdd.HasDisk) Hdd.UpdateSample(sample);
                            else if (Ssd.HasDisk) Ssd.UpdateSample(sample);
                            return;
                        }

                        if (sample.ReadBytesPerSec is double r) ReadSpeed = FormatSpeed(r);
                        if (sample.WriteBytesPerSec is double w) WriteSpeed = FormatSpeed(w);
                        if (sample.ActivePercent is double a)
                        {
                            ActiveTimeValue = a;
                            ActiveTime = $"{a}%";
                        }
                        if (sample.ResponseMs is double ms) AverageResponseTime = $"{ms:F1} ms";

                        // Route the same sample to whichever sub-VM holds the disk
                        if (Hdd.HasDisk) Hdd.UpdateSample(sample);
                        else if (Ssd.HasDisk) Ssd.UpdateSample(sample);
                    });
                }
            }
            catch { }
        }
    }

    private void ApplySampleToFlat(DiskInfo d)
    {
        if (d.NotSupported)
        {
            string msg = d.NotSupportedMessage ?? "N/A";
            ReadSpeed = msg;
            WriteSpeed = msg;
            ActiveTime = msg;
            AverageResponseTime = msg;
            return;
        }

        if (d.ReadBytesPerSec is double r) ReadSpeed = FormatSpeed(r);
        if (d.WriteBytesPerSec is double w) WriteSpeed = FormatSpeed(w);
        if (d.ActivePercent is double a)
        {
            ActiveTimeValue = a;
            ActiveTime = $"{a}%";
        }
        if (d.ResponseMs is double ms) AverageResponseTime = $"{ms:F1} ms";
    }

    private static bool IsSsd(DiskInfo d)
        => d.Type == "SSD" || d.Type == "NVMe SSD" || d.Type == "Unknown";

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

    private string _activeTime = "0%";
    public string ActiveTime { get => _activeTime; set => SetProperty(ref _activeTime, value); }

    private double _activeTimeValue;
    public double ActiveTimeValue { get => _activeTimeValue; set => SetProperty(ref _activeTimeValue, value); }

    private string _averageResponseTime = "0.0 ms";
    public string AverageResponseTime { get => _averageResponseTime; set => SetProperty(ref _averageResponseTime, value); }

    private string _readSpeed = "0 KB/s";
    public string ReadSpeed { get => _readSpeed; set => SetProperty(ref _readSpeed, value); }

    private string _writeSpeed = "0 KB/s";
    public string WriteSpeed { get => _writeSpeed; set => SetProperty(ref _writeSpeed, value); }

    private string _capacity = "-";
    public string Capacity { get => _capacity; set => SetProperty(ref _capacity, value); }

    private string _formatted = "-";
    public string Formatted { get => _formatted; set => SetProperty(ref _formatted, value); }

    private string _systemDisk = "-";
    public string SystemDisk { get => _systemDisk; set => SetProperty(ref _systemDisk, value); }

    private string _pageFile = "-";
    public string PageFile { get => _pageFile; set => SetProperty(ref _pageFile, value); }

    private string _usedText = "-";
    public string UsedText { get => _usedText; set => SetProperty(ref _usedText, value); }

    private string _freeText = "-";
    public string FreeText { get => _freeText; set => SetProperty(ref _freeText, value); }

    private string _appsAndDataText = "-";
    public string AppsAndDataText { get => _appsAndDataText; set => SetProperty(ref _appsAndDataText, value); }

    private string _systemReservedText = "-";
    public string SystemReservedText { get => _systemReservedText; set => SetProperty(ref _systemReservedText, value); }

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
        SystemDisk = d.SystemDisk ?? "No";
        PageFile = d.PageFile ?? "No";
        UsedText = d.UsedText ?? "-";
        FreeText = d.FreeText ?? "-";
        AppsAndDataText = d.AppsAndDataText ?? "-";
        SystemReservedText = d.SystemReservedText ?? "-";
    }

    public void UpdateSample(DiskInfo d)
    {
        if (d.NotSupported)
        {
            string msg = d.NotSupportedMessage ?? "N/A";
            ReadSpeed = msg;
            WriteSpeed = msg;
            ActiveTime = msg;
            AverageResponseTime = msg;
            return;
        }

        if (d.ReadBytesPerSec is double r) ReadSpeed = DiskViewModel.FormatSpeed(r);
        if (d.WriteBytesPerSec is double w) WriteSpeed = DiskViewModel.FormatSpeed(w);
        if (d.ActivePercent is double a)
        {
            ActiveTimeValue = a;
            ActiveTime = $"{a}%";
        }
        if (d.ResponseMs is double ms) AverageResponseTime = $"{ms:F1} ms";
    }

    public void UpdateSample(DiskSample d)
    {
        if (d.NotSupported)
        {
            string msg = d.NotSupportedMessage ?? "N/A";
            ReadSpeed = msg;
            WriteSpeed = msg;
            ActiveTime = msg;
            AverageResponseTime = msg;
            return;
        }

        if (d.ReadBytesPerSec is double r) ReadSpeed = DiskViewModel.FormatSpeed(r);
        if (d.WriteBytesPerSec is double w) WriteSpeed = DiskViewModel.FormatSpeed(w);
        if (d.ActivePercent is double a)
        {
            ActiveTimeValue = a;
            ActiveTime = $"{a}%";
        }
        if (d.ResponseMs is double ms) AverageResponseTime = $"{ms:F1} ms";
    }

    public void ResetToEmpty()
    {
        HasDisk = false;
        DiskName = "";
        ModelName = EmptyLabel;
        Capacity = "-";
        Formatted = "-";
        SystemDisk = "-";
        PageFile = "-";
        UsedText = "-";
        FreeText = "-";
        AppsAndDataText = "-";
        SystemReservedText = "-";
        ActiveTime = "0%";
        ActiveTimeValue = 0;
        AverageResponseTime = "0.0 ms";
        ReadSpeed = "0 KB/s";
        WriteSpeed = "0 KB/s";
    }
}