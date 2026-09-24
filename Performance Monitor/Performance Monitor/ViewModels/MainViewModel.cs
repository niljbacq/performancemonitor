using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskManager.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Reusable view model instances to prevent memory leaks and redundant background loops
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

    // --- Active Tab State Checks ---
    public bool IsHomeActive => CurrentView is HomeViewModel;
    public bool IsCpuActive => CurrentView is CpuViewModel;
    public bool IsMemoryActive => CurrentView is MemoryViewModel;
    public bool IsDiskActive => CurrentView is DiskViewModel;
    public bool IsWifiActive => CurrentView is WifiViewModel;
    public bool IsGpuActive => CurrentView is GpuViewModel;

    public MainViewModel()
    {
        // Initialize child view models once
        _homeViewModel = new HomeViewModel();
        _cpuViewModel = new CpuViewModel();
        _memoryViewModel = new MemoryViewModel();
        _diskViewModel = new DiskViewModel();
        _wifiViewModel = new WifiViewModel();
        _gpuViewModel = new GpuViewModel();

        // Set default view on launch
        _currentView = _homeViewModel;
    }

    // --- Navigation Commands ---
    [RelayCommand]
    private void SelectHome() => CurrentView = _homeViewModel;

    [RelayCommand]
    private void SelectCpu() => CurrentView = _cpuViewModel;

    [RelayCommand]
    private void SelectMemory() => CurrentView = _memoryViewModel;

    [RelayCommand]
    private void SelectDisk() => CurrentView = _diskViewModel;

    [RelayCommand]
    private void SelectWifi() => CurrentView = _wifiViewModel;

    [RelayCommand]
    private void SelectGpu() => CurrentView = _gpuViewModel;
}
