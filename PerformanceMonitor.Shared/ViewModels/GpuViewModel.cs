using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class GpuViewModel : ViewModelBase
{
    // ---- Flat properties (kept for compatibility with the single-GPU layout until Stage 5) ----
    // null = no value from the provider, so nothing is shown (never a fake "N/A" or "0%").
    private string _gpuName = "Detecting GPU...";
    public string GpuName { get => _gpuName; set => SetProperty(ref _gpuName, value); }

    private string? _utilization;
    public string? Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private bool _hasUtilization;
    public bool HasUtilization { get => _hasUtilization; set => SetProperty(ref _hasUtilization, value); }

    private double _utilizationValue = 0;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private string? _temperature;
    public string? Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }

    private string? _driverVersion;
    public string? DriverVersion { get => _driverVersion; set => SetProperty(ref _driverVersion, value); }

    private string? _driverDate;
    public string? DriverDate { get => _driverDate; set => SetProperty(ref _driverDate, value); }

    private string? _directXVersion;
    public string? DirectXVersion { get => _directXVersion; set => SetProperty(ref _directXVersion, value); }

    private string? _physicalLocation;
    public string? PhysicalLocation { get => _physicalLocation; set => SetProperty(ref _physicalLocation, value); }

    private string? _memoryUsage;
    public string? MemoryUsage { get => _memoryUsage; set => SetProperty(ref _memoryUsage, value); }

    private string? _sharedMemoryUsage;
    public string? SharedMemoryUsage { get => _sharedMemoryUsage; set => SetProperty(ref _sharedMemoryUsage, value); }

    [ObservableProperty] private string? _gpuVendor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplay))]
    private string? _displayResolution;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplay))]
    private string? _refreshRateText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplay))]
    private string? _hdrText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplay))]
    private string? _wideColorText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThermal))]
    private string? _thermalStatus;

    public bool HasDisplay =>
        DisplayResolution != null || RefreshRateText != null || HdrText != null || WideColorText != null;
    public bool HasThermal => ThermalStatus != null;

    // ---- Integrated / Dedicated split ----
    public GpuSubViewModel Integrated { get; } = new GpuSubViewModel("Integrated", "No integrated GPU");
    public GpuSubViewModel Dedicated { get; } = new GpuSubViewModel("Dedicated", "No dedicated GPU");

    private readonly IGpuProvider _provider;
    private readonly IGpuListProvider? _listProvider;
    private readonly IGpuEnvironmentProvider? _environment;
    private CancellationTokenSource? _cts;
    private bool _specsLoaded;

    public GpuViewModel() : this(ProviderFactory.CreateGpu()) { }

    public GpuViewModel(IGpuProvider provider)
    {
        _provider = provider;
        _listProvider = provider as IGpuListProvider;
        _environment = provider as IGpuEnvironmentProvider;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        if (!_specsLoaded)
        {
            _specsLoaded = true;
            _ = LoadGpuSpecsAsync();
        }

        _ = StartMonitoringAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoadGpuSpecsAsync()
    {
        if (_listProvider != null)
        {
            var list = await Task.Run(() => _listProvider.GetGpuList());
            ApplyListSpecs(list);
        }
        else
        {
            var s = await Task.Run(() => _provider.GetSpecs());
            ApplySingleSpec(s);
        }

        if (_environment != null)
            ApplyEnvironment(await Task.Run(() => _environment.GetEnvironment()));
    }

    private void ApplyEnvironment(GpuEnvironment env)
    {
        DisplayResolution = env.DisplayResolution;
        RefreshRateText = env.RefreshRate;
        HdrText = env.Hdr;
        WideColorText = env.WideColor;
        ThermalStatus = env.ThermalStatus;
    }

    private void ApplySingleSpec(GpuSpecs s)
    {
        if (s.Name != null) GpuName = s.Name;
        if (s.DriverVersion != null) DriverVersion = s.DriverVersion;
        if (s.DriverDate != null) DriverDate = s.DriverDate;
        if (s.GraphicsApi != null) DirectXVersion = s.GraphicsApi;
        if (s.PhysicalLocation != null) PhysicalLocation = s.PhysicalLocation;
        GpuVendor = s.Vendor;

        // Legacy path (no list provider): treat the single GPU as a standalone card.
        var info = new GpuInfo
        {
            Name = s.Name,
            DriverVersion = s.DriverVersion,
            DriverDate = s.DriverDate,
            GraphicsApi = s.GraphicsApi,
            PhysicalLocation = s.PhysicalLocation,
            Vendor = s.Vendor
        };

        if (info.Kind == "Dedicated")
        {
            Dedicated.Update(info);
            Integrated.ResetToEmpty();
        }
        else
        {
            // Integrated or Unknown: show it under Integrated so the tab isn't empty.
            Integrated.Update(info);
            Dedicated.ResetToEmpty();
        }
    }

    private void ApplyListSpecs(IReadOnlyList<GpuInfo> list)
    {
        var igpu = PickPrimary(list, GpuInfo => GpuInfo.Kind == "Integrated");
        var dgpu = PickPrimary(list, GpuInfo => GpuInfo.Kind == "Dedicated");

        if (igpu != null) Integrated.Update(igpu); else Integrated.ResetToEmpty();
        if (dgpu != null) Dedicated.Update(dgpu); else Dedicated.ResetToEmpty();

        var primary = GpuMapPrimary(list);
        if (primary != null)
        {
            GpuName = primary.Name ?? "GPU";
            DriverVersion = primary.DriverVersion;
            DriverDate = primary.DriverDate;
            DirectXVersion = primary.GraphicsApi;
            PhysicalLocation = primary.PhysicalLocation;
            MemoryUsage = primary.MemoryUsage;
            SharedMemoryUsage = primary.SharedMemoryUsage;
            GpuVendor = primary.Vendor;
        }
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
                if (tick % 2 != 1) continue;

                try
                {
                    if (_listProvider != null)
                    {
                        var samples = _listProvider.SampleGpuList();
                        var igpu = PickPrimary(samples, GpuInfo => GpuInfo.Kind == "Integrated");
                        var dgpu = PickPrimary(samples, GpuInfo => GpuInfo.Kind == "Dedicated");
                        var env = _environment?.GetEnvironment();

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (igpu != null) Integrated.UpdateSample(igpu); else Integrated.ResetToEmpty();
                            if (dgpu != null) Dedicated.UpdateSample(dgpu); else Dedicated.ResetToEmpty();

                            var primary = GpuMapPrimary(samples);
                            if (primary != null) ApplySampleToFlat(primary);
                            else ClearFlatSample();

                            if (env != null) ApplyEnvironment(env);
                        });
                    }
                    else
                    {
                        var sample = _provider.Sample();

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (sample == null)
                            {
                                ClearFlatSample();
                                return;
                            }

                            ApplySampleToFlat(sample);

                            // Route the same sample to whichever sub-VM holds the GPU.
                            if (Dedicated.HasGpu) Dedicated.UpdateSample(sample);
                            else if (Integrated.HasGpu) Integrated.UpdateSample(sample);
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
        HasUtilization = false;
        Utilization = null;
        UtilizationValue = 0;
        Temperature = null;
        MemoryUsage = null;
        SharedMemoryUsage = null;
    }

    private void ApplySampleToFlat(GpuInfo d)
    {
        HasUtilization = d.Utilization is double;
        UtilizationValue = d.Utilization ?? 0;
        Utilization = d.Utilization is double u ? $"{u:0}%" : d.UtilizationText;
        Temperature = d.TemperatureC is double t ? $"{t} °C" : d.TemperatureText;
        MemoryUsage = d.MemoryUsage;
        SharedMemoryUsage = d.SharedMemoryUsage;
    }

    private void ApplySampleToFlat(GpuSample d)
    {
        HasUtilization = d.Utilization is double;
        UtilizationValue = d.Utilization ?? 0;
        Utilization = d.Utilization is double u ? $"{u:0}%" : d.UtilizationText;
        Temperature = d.TemperatureC is double t ? $"{t} °C" : d.TemperatureText;
        MemoryUsage = d.MemoryUsage;
    }

    private static GpuInfo? PickPrimary(IReadOnlyList<GpuInfo> list, Func<GpuInfo, bool> match)
    {
        foreach (var g in list)
        {
            if (match(g)) return g;
        }
        return null;
    }

    private static GpuInfo? GpuMapPrimary(IReadOnlyList<GpuInfo> list)
    {
        // First Dedicated, else first of any kind (mirrors GpuMap.Primary).
        foreach (var g in list)
        {
            if (g.Kind == "Dedicated") return g;
        }
        return list.Count > 0 ? list[0] : null;
    }
}

public partial class GpuSubViewModel : ViewModelBase
{
    public string EmptyLabel { get; }
    public string TypeLabel { get; }

    private bool _hasGpu;
    public bool HasGpu { get => _hasGpu; set => SetProperty(ref _hasGpu, value); }

    private string _gpuName = "";
    public string GpuName { get => _gpuName; set => SetProperty(ref _gpuName, value); }

    // null = no value from the provider, so the row is hidden.
    private string? _utilization;
    public string? Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private bool _hasUtilizationValue;
    public bool HasUtilizationValue { get => _hasUtilizationValue; set => SetProperty(ref _hasUtilizationValue, value); }

    private double _utilizationValue;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private string? _temperature;
    public string? Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }

    private string? _driverVersion;
    public string? DriverVersion { get => _driverVersion; set => SetProperty(ref _driverVersion, value); }

    private string? _driverDate;
    public string? DriverDate { get => _driverDate; set => SetProperty(ref _driverDate, value); }

    private string? _graphicsApi;
    public string? GraphicsApi { get => _graphicsApi; set => SetProperty(ref _graphicsApi, value); }

    private string? _physicalLocation;
    public string? PhysicalLocation { get => _physicalLocation; set => SetProperty(ref _physicalLocation, value); }

    private string? _memoryUsage;
    public string? MemoryUsage { get => _memoryUsage; set => SetProperty(ref _memoryUsage, value); }

    private string? _sharedMemoryUsage;
    public string? SharedMemoryUsage { get => _sharedMemoryUsage; set => SetProperty(ref _sharedMemoryUsage, value); }

    private string? _vendor;
    public string? Vendor { get => _vendor; set => SetProperty(ref _vendor, value); }

    public GpuSubViewModel(string typeLabel, string emptyLabel)
    {
        TypeLabel = typeLabel;
        EmptyLabel = emptyLabel;
    }

    public void Update(GpuInfo g)
    {
        HasGpu = true;
        GpuName = g.Name ?? "GPU";
        DriverVersion = g.DriverVersion;
        DriverDate = g.DriverDate;
        GraphicsApi = g.GraphicsApi;
        PhysicalLocation = g.PhysicalLocation;
        MemoryUsage = g.MemoryUsage;
        SharedMemoryUsage = g.SharedMemoryUsage;
        Vendor = g.Vendor;
    }

    public void UpdateSample(GpuInfo g)
    {
        HasUtilizationValue = g.Utilization is double;
        UtilizationValue = g.Utilization ?? 0;
        Utilization = g.Utilization is double u ? $"{u:0}%" : g.UtilizationText;
        Temperature = g.TemperatureC is double t ? $"{t} °C" : g.TemperatureText;
        MemoryUsage = g.MemoryUsage;
        SharedMemoryUsage = g.SharedMemoryUsage;
        Vendor = g.Vendor;
    }

    public void UpdateSample(GpuSample s)
    {
        HasUtilizationValue = s.Utilization is double;
        UtilizationValue = s.Utilization ?? 0;
        Utilization = s.Utilization is double u ? $"{u:0}%" : s.UtilizationText;
        Temperature = s.TemperatureC is double t ? $"{t} °C" : s.TemperatureText;
        MemoryUsage = s.MemoryUsage;
    }

    public void ResetToEmpty()
    {
        HasGpu = false;
        GpuName = EmptyLabel;
        Utilization = null;
        HasUtilizationValue = false;
        UtilizationValue = 0;
        Temperature = null;
        DriverVersion = null;
        DriverDate = null;
        GraphicsApi = null;
        PhysicalLocation = null;
        MemoryUsage = null;
        SharedMemoryUsage = null;
        Vendor = null;
    }
}
