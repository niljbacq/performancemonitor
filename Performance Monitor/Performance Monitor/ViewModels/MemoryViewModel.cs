using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class MemoryViewModel : ViewModelBase
{
    private readonly MemoryMonitor _monitor = new();

    private string _usedMemoryText = "0 GB";
    public string UsedMemoryText
    {
        get => _usedMemoryText;
        set => SetProperty(ref _usedMemoryText, value);
    }

    private string _totalMemoryText = "0 GB";
    public string TotalMemoryText
    {
        get => _totalMemoryText;
        set => SetProperty(ref _totalMemoryText, value);
    }

    private string _memoryUsagePercent = "0%";
    public string MemoryUsagePercent
    {
        get => _memoryUsagePercent;
        set => SetProperty(ref _memoryUsagePercent, value);
    }

    private double _memoryUsagePercentValue = 0;
    public double MemoryUsagePercentValue
    {
        get => _memoryUsagePercentValue;
        set => SetProperty(ref _memoryUsagePercentValue, value);
    }

    public MemoryViewModel()
    {
        _ = StartMonitoringAsync();
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // Resume off the UI thread, same as every other tab's monitoring loop -
        // reading memory status is cheap, but there's no reason to make an
        // exception here just because this used to live on the shell VM.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            var (totalGB, usedGB, percent) = _monitor.GetLiveMemoryUsage();

            Dispatcher.UIThread.Post(() =>
            {
                UsedMemoryText = $"{usedGB} GB";
                TotalMemoryText = $"{totalGB} GB";
                MemoryUsagePercent = $"{percent}%";
                MemoryUsagePercentValue = percent;
            });
        }
    }
}
