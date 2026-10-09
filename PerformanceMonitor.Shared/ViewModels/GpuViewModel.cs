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
    private string _gpuName = "Detecting GPU...";
    public string GpuName { get => _gpuName; set => SetProperty(ref _gpuName, value); }

    private string _utilization = "0%";
    public string Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private double _utilizationValue = 0;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private string _temperature = "N/A";
    public string Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }

    private string _driverVersion = "Unknown";
    public string DriverVersion { get => _driverVersion; set => SetProperty(ref _driverVersion, value); }

    private string _driverDate = "Unknown";
    public string DriverDate { get => _driverDate; set => SetProperty(ref _driverDate, value); }

    private string _directXVersion = "N/A";
    public string DirectXVersion { get => _directXVersion; set => SetProperty(ref _directXVersion, value); }

    private string _physicalLocation = "Unknown";
    public string PhysicalLocation { get => _physicalLocation; set => SetProperty(ref _physicalLocation, value); }

    private string _memoryUsage = "N/A";
    public string MemoryUsage { get => _memoryUsage; set => SetProperty(ref _memoryUsage, value); }

    private string _sharedMemoryUsage = "N/A";
    public string SharedMemoryUsage { get => _sharedMemoryUsage; set => SetProperty(ref _sharedMemoryUsage, value); }

    [ObservableProperty] private string? _gpuVendor;

    // ---- Integrated / Dedicated split ----
    public GpuSubViewModel Integrated { get; } = new GpuSubViewModel("Integrated", "No integrated GPU");
    public GpuSubViewModel Dedicated { get; } = new GpuSubViewModel("Dedicated", "No dedicated GPU");

    private readonly IGpuProvider _provider;
    private readonly IGpuListProvider? _listProvider;

    public GpuViewModel() : this(ProviderFactory.CreateGpu()) { }

    public GpuViewModel(IGpuProvider provider)
    {
        _provider = provider;
        _listProvider = provider as IGpuListProvider;
        _ = LoadGpuSpecsAsync();
        _ = StartMonitoringAsync();
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
            DriverVersion = primary.DriverVersion ?? "Unknown";
            DriverDate = primary.DriverDate ?? "Unknown";
            DirectXVersion = primary.GraphicsApi ?? "N/A";
            PhysicalLocation = primary.PhysicalLocation ?? "Unknown";
            MemoryUsage = primary.MemoryUsage ?? "N/A";
            SharedMemoryUsage = primary.SharedMemoryUsage ?? "N/A";
            GpuVendor = primary.Vendor;
        }
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
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

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (igpu != null) Integrated.UpdateSample(igpu);
                        if (dgpu != null) Dedicated.UpdateSample(dgpu);

                        var primary = GpuMapPrimary(samples);
                        if (primary != null) ApplySampleToFlat(primary);
                    });
                }
                else
                {
                    var sample = _provider.Sample();
                    if (sample == null) continue;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (sample.Utilization is double u)
                        {
                            UtilizationValue = u;
                            Utilization = $"{u}%";
                        }
                        if (sample.UtilizationText != null) Utilization = sample.UtilizationText;
                        if (sample.TemperatureC is double t) Temperature = $"{t} °C";
                        if (sample.TemperatureText != null) Temperature = sample.TemperatureText;
                        if (sample.MemoryUsage != null) MemoryUsage = sample.MemoryUsage;

                        // Route the same sample to whichever sub-VM holds the GPU.
                        if (Dedicated.HasGpu) Dedicated.UpdateSample(sample);
                        else if (Integrated.HasGpu) Integrated.UpdateSample(sample);
                    });
                }
            }
            catch { }
        }
    }

    private void ApplySampleToFlat(GpuInfo d)
    {
        if (d.Utilization is double u)
        {
            UtilizationValue = u;
            Utilization = $"{u}%";
        }
        if (d.UtilizationText != null) Utilization = d.UtilizationText;
        if (d.TemperatureC is double t) Temperature = $"{t} °C";
        if (d.TemperatureText != null) Temperature = d.TemperatureText;
        if (d.MemoryUsage != null) MemoryUsage = d.MemoryUsage;
        if (d.SharedMemoryUsage != null) SharedMemoryUsage = d.SharedMemoryUsage;
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

    private string _utilization = "0%";
    public string Utilization { get => _utilization; set => SetProperty(ref _utilization, value); }

    private double _utilizationValue;
    public double UtilizationValue { get => _utilizationValue; set => SetProperty(ref _utilizationValue, value); }

    private string _temperature = "N/A";
    public string Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }

    private string _driverVersion = "Unknown";
    public string DriverVersion { get => _driverVersion; set => SetProperty(ref _driverVersion, value); }

    private string _driverDate = "Unknown";
    public string DriverDate { get => _driverDate; set => SetProperty(ref _driverDate, value); }

    private string _graphicsApi = "N/A";
    public string GraphicsApi { get => _graphicsApi; set => SetProperty(ref _graphicsApi, value); }

    private string _physicalLocation = "Unknown";
    public string PhysicalLocation { get => _physicalLocation; set => SetProperty(ref _physicalLocation, value); }

    private string _memoryUsage = "N/A";
    public string MemoryUsage { get => _memoryUsage; set => SetProperty(ref _memoryUsage, value); }

    private string _sharedMemoryUsage = "N/A";
    public string SharedMemoryUsage { get => _sharedMemoryUsage; set => SetProperty(ref _sharedMemoryUsage, value); }

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
        DriverVersion = g.DriverVersion ?? "Unknown";
        DriverDate = g.DriverDate ?? "Unknown";
        GraphicsApi = g.GraphicsApi ?? "N/A";
        PhysicalLocation = g.PhysicalLocation ?? "Unknown";
        MemoryUsage = g.MemoryUsage ?? "N/A";
        SharedMemoryUsage = g.SharedMemoryUsage ?? "N/A";
        Vendor = g.Vendor;
    }

    public void UpdateSample(GpuInfo g)
    {
        if (g.Utilization is double u)
        {
            UtilizationValue = u;
            Utilization = $"{u}%";
        }
        if (g.UtilizationText != null) Utilization = g.UtilizationText;
        if (g.TemperatureC is double t) Temperature = $"{t} °C";
        if (g.TemperatureText != null) Temperature = g.TemperatureText;
        if (g.MemoryUsage != null) MemoryUsage = g.MemoryUsage;
        if (g.SharedMemoryUsage != null) SharedMemoryUsage = g.SharedMemoryUsage;
        if (g.Vendor != null) Vendor = g.Vendor;
    }

    public void UpdateSample(GpuSample s)
    {
        if (s.Utilization is double u)
        {
            UtilizationValue = u;
            Utilization = $"{u}%";
        }
        if (s.UtilizationText != null) Utilization = s.UtilizationText;
        if (s.TemperatureC is double t) Temperature = $"{t} °C";
        if (s.TemperatureText != null) Temperature = s.TemperatureText;
        if (s.MemoryUsage != null) MemoryUsage = s.MemoryUsage;
    }

    public void ResetToEmpty()
    {
        HasGpu = false;
        GpuName = EmptyLabel;
        Utilization = "0%";
        UtilizationValue = 0;
        Temperature = "N/A";
        DriverVersion = "Unknown";
        DriverDate = "Unknown";
        GraphicsApi = "N/A";
        PhysicalLocation = "Unknown";
        MemoryUsage = "N/A";
        SharedMemoryUsage = "N/A";
        Vendor = null;
    }
}
