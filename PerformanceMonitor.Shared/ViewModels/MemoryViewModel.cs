using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class MemoryViewModel : ViewModelBase
{
    private readonly IMemoryProvider _provider;
    private CancellationTokenSource? _cts;

    private string? _usedMemoryText;
    public string? UsedMemoryText { get => _usedMemoryText; set => SetProperty(ref _usedMemoryText, value); }

    private string? _totalMemoryText;
    public string? TotalMemoryText { get => _totalMemoryText; set => SetProperty(ref _totalMemoryText, value); }

    private string? _memoryUsagePercent;
    public string? MemoryUsagePercent { get => _memoryUsagePercent; set => SetProperty(ref _memoryUsagePercent, value); }

    private double _memoryUsagePercentValue = 0;
    public double MemoryUsagePercentValue { get => _memoryUsagePercentValue; set => SetProperty(ref _memoryUsagePercentValue, value); }

    [ObservableProperty] private string? _swapText;
    [ObservableProperty] private string? _activeText;
    [ObservableProperty] private string? _inactiveText;
    [ObservableProperty] private string? _slabText;
    [ObservableProperty] private string? _pressureText;
    [ObservableProperty] private IReadOnlyList<MemoryDetail> _details = Array.Empty<MemoryDetail>();

    public MemoryViewModel() : this(ProviderFactory.CreateMemory()) { }

    public MemoryViewModel(IMemoryProvider provider)
    {
        _provider = provider;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = StartMonitoringAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
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
                    var m = _provider.GetUsage();
                    if (m.TotalGB <= 0) continue;   // read failed; keep the rows blank

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
                        PressureText = m.PressureText;
                        Details = m.Details;
                    });
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }
}