using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class CpuViewModel : ViewModelBase
{
    private string _cpuName = "Detecting CPU...";
    public string CpuName { get => _cpuName; set => SetProperty(ref _cpuName, value); }

    // Raw hardware/SoC id, shown under the marketing name (Android only).
    private string? _hardwareName;
    public string? HardwareName { get => _hardwareName; set => SetProperty(ref _hardwareName, value); }

    private string? _utilization;
    public string? Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private double _utilizationValue = 0;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private bool _hasUtilization = true;
    public bool HasUtilization { get => _hasUtilization; set => SetProperty(ref _hasUtilization, value); }

    [ObservableProperty] private string? _usageNote;

    private string? _speed;
    public string? Speed { get => _speed; set => SetProperty(ref _speed, value); }

    // Null = not available on this platform, so the row hides.
    private string? _processes;
    public string? Processes 
    { 
        get => _processes; 
        set { if (SetProperty(ref _processes, value)) OnPropertyChanged(nameof(HasProcesses)); }
    }
    public bool HasProcesses => !string.IsNullOrEmpty(_processes);

    private string? _threads;
    public string? Threads 
    { 
        get => _threads; 
        set { if (SetProperty(ref _threads, value)) OnPropertyChanged(nameof(HasThreads)); }
    }
    public bool HasThreads => !string.IsNullOrEmpty(_threads);

    private string? _handles;
    public string? Handles 
    { 
        get => _handles; 
        set { if (SetProperty(ref _handles, value)) OnPropertyChanged(nameof(HasHandles)); }
    }
    public bool HasHandles => !string.IsNullOrEmpty(_handles);

    private string _uptime = "0:00:00:00";
    public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }

    private string? _baseSpeed;
    public string? BaseSpeed { get => _baseSpeed; set => SetProperty(ref _baseSpeed, value); }

    private string? _sockets;
    public string? Sockets { get => _sockets; set => SetProperty(ref _sockets, value); }

    private string _cores = Environment.ProcessorCount.ToString();
    public string Cores { get => _cores; set => SetProperty(ref _cores, value); }

    private string _logicalProcessors = Environment.ProcessorCount.ToString();
    public string LogicalProcessors { get => _logicalProcessors; set => SetProperty(ref _logicalProcessors, value); }

    private string? _virtualization;
    public string? Virtualization { get => _virtualization; set => SetProperty(ref _virtualization, value); }

    private string? _l1Cache;
    public string? L1Cache { get => _l1Cache; set => SetProperty(ref _l1Cache, value); }

    private string? _l2Cache;
    public string? L2Cache { get => _l2Cache; set => SetProperty(ref _l2Cache, value); }

    private string? _l3Cache;
    public string? L3Cache { get => _l3Cache; set => SetProperty(ref _l3Cache, value); }

    [ObservableProperty] private string? _frequencyMinText;
    [ObservableProperty] private string? _frequencyMaxText;
    [ObservableProperty] private string? _cpuGovernor;
    [ObservableProperty] private string? _supportedAbis;
    [ObservableProperty] private string? _clusterText;
    [ObservableProperty] private string? _perCoreFrequencyText;
    [ObservableProperty] private string? _temperatureText;
    [ObservableProperty] private string? _pressureText;
    [ObservableProperty] private string? _frequencyResidencyText;
    [ObservableProperty] private string? _appUsageText;

    private readonly ICpuProvider _provider;
    private CancellationTokenSource? _cts;
    private bool _specsLoaded;

    public CpuViewModel() : this(ProviderFactory.CreateCpu()) { }

    public CpuViewModel(ICpuProvider provider)
    {
        _provider = provider;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        if (!_specsLoaded)
        {
            _specsLoaded = true;
            _ = LoadCpuSpecsAsync();
        }

        _ = StartMonitoringAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoadCpuSpecsAsync()
    {
        var s = await Task.Run(() => _provider.GetSpecs());

        if (s.Name != null) CpuName = s.Name;
        HardwareName = s.Name != null && !string.Equals(s.Name, CpuName, StringComparison.OrdinalIgnoreCase)
            ? s.Name
            : null;
        if (s.BaseSpeed != null) BaseSpeed = s.BaseSpeed;
        if (s.Speed != null) Speed = s.Speed;
        if (s.Cores != null) Cores = s.Cores;
        if (s.LogicalProcessors != null) LogicalProcessors = s.LogicalProcessors;
        if (s.Sockets != null) Sockets = s.Sockets;
        if (s.Virtualization != null) Virtualization = s.Virtualization;
        if (s.L1Cache != null) L1Cache = s.L1Cache;
        if (s.L2Cache != null) L2Cache = s.L2Cache;
        if (s.L3Cache != null) L3Cache = s.L3Cache;
        if (s.TemperatureText != null) TemperatureText = s.TemperatureText;

        FrequencyMinText = s.FrequencyMinText;
        FrequencyMaxText = s.FrequencyMaxText;
        CpuGovernor = s.CpuGovernor;
        SupportedAbis = s.SupportedAbis;
        ClusterText = s.ClusterText;
        PressureText = s.PressureText;
    }

    private async Task StartMonitoringAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                tick++;
                try
                {
                    float rawUsage = _provider.GetUsagePercent();
                    if (rawUsage < 0) continue;   // provider is still priming its baseline

                    string? usageNote = _provider.UsageUnavailableMessage;
                    int cpuPercent = (int)Math.Round(rawUsage);

                    TimeSpan up = TimeSpan.FromMilliseconds(_provider.UptimeMilliseconds);
                    string uptimeText = $"{up.Days}:{up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";

                    bool statsDue = tick % 3 == 1;
                    string? statsNote = _provider.ProcessStatsUnavailableMessage;
                    ProcessStats? stats = statsDue && statsNote == null ? _provider.GetProcessStats() : null;

                    string? perCoreText = _provider.PerCoreFrequencyText;
                    string? residencyText = _provider.FrequencyResidencyText;
                    string? appUsageText = _provider.AppUsageText;

                    Dispatcher.UIThread.Post(() =>
                    {
                        HasUtilization = usageNote == null;
                        UsageNote = usageNote;
                        UtilizationValue = usageNote == null ? cpuPercent : 0;
                        Utilization = usageNote ?? $"{cpuPercent}%";
                        Uptime = uptimeText;

                        if (perCoreText != null) PerCoreFrequencyText = perCoreText;
                        if (residencyText != null) FrequencyResidencyText = residencyText;
                        if (appUsageText != null) AppUsageText = appUsageText;

                        if (stats != null)
                        {
                            Processes = stats.Processes?.ToString();
                            Threads = stats.Threads?.ToString();
                            Handles = stats.Handles;
                        }
                        else if (statsDue && statsNote != null)
                        {
                            // Platform has no process stats at all; clear the rows so they hide.
                            Processes = null;
                            Threads = null;
                            Handles = null;
                        }
                    });
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }
}
