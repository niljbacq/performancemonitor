using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class SystemViewModel : ViewModelBase
{
    [ObservableProperty] private string? _operatingSystem;
    [ObservableProperty] private string? _architecture;
    [ObservableProperty] private string? _processArchitecture;
    [ObservableProperty] private string? _runtime;
    [ObservableProperty] private string? _avalonia;
    [ObservableProperty] private string? _kernel;
    [ObservableProperty] private string? _distro;
    [ObservableProperty] private string? _machineName;
    [ObservableProperty] private string? _processorCount;

    [ObservableProperty] private string? _manufacturer;
    [ObservableProperty] private string? _deviceModel;
    [ObservableProperty] private string? _deviceHardware;
    [ObservableProperty] private string? _socModel;
    [ObservableProperty] private string? _androidRelease;
    [ObservableProperty] private string? _androidSdk;
    [ObservableProperty] private string? _androidBoard;

    private readonly ISystemInfoProvider _provider;

    public SystemViewModel() : this(ProviderFactory.CreateSystem()) { }

    public SystemViewModel(ISystemInfoProvider provider)
    {
        _provider = provider;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var info = await Task.Run(() => _provider.GetInfo());

        OperatingSystem = Blank(info.OperatingSystem);
        Architecture = Blank(info.Architecture);
        ProcessArchitecture = Blank(info.ProcessArchitecture);
        Runtime = Blank(info.Runtime);
        Avalonia = Blank(info.Avalonia);
        Kernel = Blank(info.Kernel);
        Distro = Blank(info.Distro);
        MachineName = Blank(info.MachineName);
        ProcessorCount = Blank(info.ProcessorCount);

        Manufacturer = Blank(info.Manufacturer);
        DeviceModel = Blank(info.DeviceModel);
        DeviceHardware = Blank(info.DeviceHardware);
        SocModel = Blank(info.SocModel);
        AndroidRelease = Blank(info.AndroidRelease);
        AndroidSdk = Blank(info.AndroidSdk);
        AndroidBoard = Blank(info.AndroidBoard);
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
