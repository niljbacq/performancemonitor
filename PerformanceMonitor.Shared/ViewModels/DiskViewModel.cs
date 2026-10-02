using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
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

    private readonly IDiskProvider _provider;

    public DiskViewModel() : this(ProviderFactory.CreateDisk()) { }

    public DiskViewModel(IDiskProvider provider)
    {
        _provider = provider;
        _ = LoadDiskSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private async Task LoadDiskSpecsAsync()
    {
        var s = await Task.Run(() => _provider.GetSpecs());

        if (s.DiskName != null) DiskName = s.DiskName;
        if (s.ModelName != null) ModelName = s.ModelName;
        if (s.Capacity != null) Capacity = s.Capacity;
        if (s.Formatted != null) Formatted = s.Formatted;
        if (s.SystemDisk != null) SystemDisk = s.SystemDisk;
        if (s.PageFile != null) PageFile = s.PageFile;
        if (s.Type != null) Type = s.Type;
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                var sample = _provider.Sample();
                if (sample == null) continue;

                Dispatcher.UIThread.Post(() =>
                {
                    if (sample.NotSupported)
                    {
                        ReadSpeed = "N/A";
                        WriteSpeed = "N/A";
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
                });
            }
            catch { }
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{Math.Round(bytesPerSec / (1024.0 * 1024.0), 1)} MB/s";
        return $"{Math.Round(bytesPerSec / 1024.0, 0)} KB/s";
    }
}