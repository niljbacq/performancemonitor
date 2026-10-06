using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class MemoryViewModel : ViewModelBase
{
    private readonly IMemoryProvider _provider;

    private string _usedMemoryText = "0 GB";
    public string UsedMemoryText { get => _usedMemoryText; set => SetProperty(ref _usedMemoryText, value); }

    private string _totalMemoryText = "0 GB";
    public string TotalMemoryText { get => _totalMemoryText; set => SetProperty(ref _totalMemoryText, value); }

    private string _memoryUsagePercent = "0%";
    public string MemoryUsagePercent { get => _memoryUsagePercent; set => SetProperty(ref _memoryUsagePercent, value); }

    private double _memoryUsagePercentValue = 0;
    public double MemoryUsagePercentValue { get => _memoryUsagePercentValue; set => SetProperty(ref _memoryUsagePercentValue, value); }

    [ObservableProperty] private string? _swapText;
    [ObservableProperty] private string? _activeText;
    [ObservableProperty] private string? _inactiveText;
    [ObservableProperty] private string? _slabText;

    public MemoryViewModel() : this(ProviderFactory.CreateMemory()) { }

    public MemoryViewModel(IMemoryProvider provider)
    {
        _provider = provider;
        _ = StartMonitoringAsync();
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                var m = _provider.GetUsage();

                Dispatcher.UIThread.Post(() =>
                {
                    UsedMemoryText = $"{m.UsedGB} GB";
                    TotalMemoryText = $"{m.TotalGB} GB";
                    MemoryUsagePercent = $"{m.Percent}%";
                    MemoryUsagePercentValue = m.Percent;

                    SwapText = m.SwapText;
                    ActiveText = m.ActiveText;
                    InactiveText = m.InactiveText;
                    SlabText = m.SlabText;
                });
            }
            catch { }
        }
    }
}