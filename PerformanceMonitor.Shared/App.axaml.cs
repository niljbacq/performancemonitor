using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TaskManager.Providers;
using TaskManager.ViewModels;
using TaskManager.Views;

namespace TaskManager;

public partial class App : Application
{
    public MainViewModel? MainViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // One background sampler owns the CSV log for the whole process.
        try { SamplingService.StartDefault(); } catch { }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainViewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = MainViewModel,
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            // Android may build the view more than once, so create ONE view model and share it.
            MainViewModel = new MainViewModel();
            var vm = MainViewModel;
            activityLifetime.MainViewFactory = () => new MainView { DataContext = vm };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            MainViewModel = new MainViewModel();
            singleView.MainView = new MainView { DataContext = MainViewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
