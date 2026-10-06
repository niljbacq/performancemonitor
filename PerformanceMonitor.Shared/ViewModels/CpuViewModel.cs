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

    private string _utilization = "0%";
    public string Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private double _utilizationValue = 0;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private string _speed = "0.00 GHz";
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }

    private string _processes = "0";
    public string Processes { get => _processes; set => SetProperty(ref _processes, value); }

    private string _threads = "0";
    public string Threads { get => _threads; set => SetProperty(ref _threads, value); }

    private string _handles = "0";
    public string Handles { get => _handles; set => SetProperty(ref _handles, value); }

    private string _uptime = "0:00:00:00";
    public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }

    private string _baseSpeed = "N/A";
    public string BaseSpeed { get => _baseSpeed; set => SetProperty(ref _baseSpeed, value); }

    private string _sockets = "1";
    public string Sockets { get => _sockets; set => SetProperty(ref _sockets, value); }

    private string _cores = Environment.ProcessorCount.ToString();
    public string Cores { get => _cores; set => SetProperty(ref _cores, value); }

    private string _logicalProcessors = Environment.ProcessorCount.ToString();
    public string LogicalProcessors { get => _logicalProcessors; set => SetProperty(ref _logicalProcessors, value); }

    private string _virtualization = "Unknown";
    public string Virtualization { get => _virtualization; set => SetProperty(ref _virtualization, value); }

    private string _l1Cache = "N/A";
    public string L1Cache { get => _l1Cache; set => SetProperty(ref _l1Cache, value); }

    private string _l2Cache = "N/A";
    public string L2Cache { get => _l2Cache; set => SetProperty(ref _l2Cache, value); }

    private string _l3Cache = "N/A";
    public string L3Cache { get => _l3Cache; set => SetProperty(ref _l3Cache, value); }

    [ObservableProperty] private string? _frequencyMinText;
    [ObservableProperty] private string? _frequencyMaxText;
    [ObservableProperty] private string? _cpuGovernor;
    [ObservableProperty] private string? _supportedAbis;
    [ObservableProperty] private string? _clusterText;
    [ObservableProperty] private string? _perCoreFrequencyText;

    private readonly ICpuProvider _provider;

    public CpuViewModel() : this(ProviderFactory.CreateCpu()) { }

    public CpuViewModel(ICpuProvider provider)
    {
        _provider = provider;
        _ = LoadCpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private async Task LoadCpuSpecsAsync()
    {
        var s = await Task.Run(() => _provider.GetSpecs());

        if (s.Name != null) CpuName = s.Name;
        if (s.BaseSpeed != null) BaseSpeed = s.BaseSpeed;
        if (s.Speed != null) Speed = s.Speed;
        if (s.Cores != null) Cores = s.Cores;
        if (s.LogicalProcessors != null) LogicalProcessors = s.LogicalProcessors;
        if (s.Sockets != null) Sockets = s.Sockets;
        if (s.Virtualization != null) Virtualization = s.Virtualization;
        if (s.L1Cache != null) L1Cache = s.L1Cache;
        if (s.L2Cache != null) L2Cache = s.L2Cache;
        if (s.L3Cache != null) L3Cache = s.L3Cache;

        FrequencyMinText = s.FrequencyMinText;
        FrequencyMaxText = s.FrequencyMaxText;
        CpuGovernor = s.CpuGovernor;
        SupportedAbis = s.SupportedAbis;
        ClusterText = s.ClusterText;
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                float rawUsage = _provider.GetUsagePercent();
                string? usageNote = _provider.UsageUnavailableMessage;
                int cpuPercent = (int)Math.Round(rawUsage);

                TimeSpan up = TimeSpan.FromMilliseconds(_provider.UptimeMilliseconds);
                string uptimeText = $"{up.Days}:{up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";

                bool statsDue = tick % 3 == 1;
                string? statsNote = _provider.ProcessStatsUnavailableMessage;
                ProcessStats? stats = statsDue && statsNote == null ? _provider.GetProcessStats() : null;

                string? perCoreText = _provider.PerCoreFrequencyText;

                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = usageNote == null ? cpuPercent : 0;
                    Utilization = usageNote ?? $"{cpuPercent}%";
                    Uptime = uptimeText;

                    if (perCoreText != null) PerCoreFrequencyText = perCoreText;

                    if (stats != null)
                    {
                        Processes = stats.Processes.ToString();
                        Threads = stats.Threads.ToString();
                        Handles = stats.Handles;
                    }
                    else if (statsDue && statsNote != null)
                    {
                        Processes = statsNote;
                        Threads = statsNote;
                        Handles = statsNote;
                    }
                });
            }
            catch { }
        }
    }
}