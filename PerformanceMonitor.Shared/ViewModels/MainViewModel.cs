using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskManager.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Reusable view model instances.
    private readonly HomeViewModel _homeViewModel;
    private readonly CpuViewModel _cpuViewModel;
    private readonly MemoryViewModel _memoryViewModel;
    private readonly DiskViewModel _diskViewModel;
    private readonly WifiViewModel _wifiViewModel;
    private readonly GpuViewModel _gpuViewModel;

    // Navigation state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeActive))]
    [NotifyPropertyChangedFor(nameof(IsCpuActive))]
    [NotifyPropertyChangedFor(nameof(IsMemoryActive))]
    [NotifyPropertyChangedFor(nameof(IsDiskActive))]
    [NotifyPropertyChangedFor(nameof(IsWifiActive))]
    [NotifyPropertyChangedFor(nameof(IsGpuActive))]
    private ViewModelBase _currentView;

    public bool IsHomeActive => CurrentView is HomeViewModel;
    public bool IsCpuActive => CurrentView is CpuViewModel;
    public bool IsMemoryActive => CurrentView is MemoryViewModel;
    public bool IsDiskActive => CurrentView is DiskViewModel;
    public bool IsWifiActive => CurrentView is WifiViewModel;
    public bool IsGpuActive => CurrentView is GpuViewModel;

    public MainViewModel()
    {
        _homeViewModel = new HomeViewModel();
        _cpuViewModel = new CpuViewModel();
        _memoryViewModel = new MemoryViewModel();
        _diskViewModel = new DiskViewModel();
        _wifiViewModel = new WifiViewModel();
        _gpuViewModel = new GpuViewModel();

        _currentView = _homeViewModel;
    }

    public CpuViewModel Cpu => _cpuViewModel;
    public MemoryViewModel Memory => _memoryViewModel;
    public GpuViewModel Gpu => _gpuViewModel;

    /// <summary>All tabs sample continuously so opening one shows live data immediately.</summary>
    public void Resume()
    {
        _cpuViewModel.Start();
        _memoryViewModel.Start();
        _diskViewModel.Start();
        _wifiViewModel.Start();
        _gpuViewModel.Start();
    }

    /// <summary>Stops all sampling when the app is backgrounded or the view is detached.</summary>
    public void Pause()
    {
        _cpuViewModel.Stop();
        _memoryViewModel.Stop();
        _diskViewModel.Stop();
        _wifiViewModel.Stop();
        _gpuViewModel.Stop();
    }

    private void Activate(ViewModelBase next) => CurrentView = next;

    [RelayCommand]
    private void SelectHome() => Activate(_homeViewModel);

    [RelayCommand]
    private void SelectCpu() => Activate(_cpuViewModel);

    [RelayCommand]
    private void SelectMemory() => Activate(_memoryViewModel);

    [RelayCommand]
    private void SelectDisk() => Activate(_diskViewModel);

    [RelayCommand]
    private void SelectWifi() => Activate(_wifiViewModel);

    [RelayCommand]
    private void SelectGpu() => Activate(_gpuViewModel);

    [RelayCommand]
    private void ToggleOverlay() => OverlayHost.Toggle?.Invoke(this);
}
