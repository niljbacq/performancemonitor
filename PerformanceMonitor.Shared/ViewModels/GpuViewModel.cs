using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class GpuViewModel : ViewModelBase
{
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

    private readonly IGpuProvider _provider;

    public GpuViewModel() : this(ProviderFactory.CreateGpu()) { }

    public GpuViewModel(IGpuProvider provider)
    {
        _provider = provider;
        _ = LoadGpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private async Task LoadGpuSpecsAsync()
    {
        var s = await Task.Run(() => _provider.GetSpecs());

        if (s.Name != null) GpuName = s.Name;
        if (s.DriverVersion != null) DriverVersion = s.DriverVersion;
        if (s.DriverDate != null) DriverDate = s.DriverDate;
        if (s.GraphicsApi != null) DirectXVersion = s.GraphicsApi;
        if (s.PhysicalLocation != null) PhysicalLocation = s.PhysicalLocation;
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
                });
            }
            catch { }
        }
    }
}