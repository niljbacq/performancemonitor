using Avalonia;

using System;

using TaskManager.Views;


namespace TaskManager;


internal class Program

{

    private static OverlayWindow? _overlay;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before BuildAvaloniaApp() has been called!
    [STAThread]
    public static void Main(string[] args)
    {
        // Create the Start Menu shortcut if it doesn't exist (Windows only)
        if (OperatingSystem.IsWindows())
        {
            ShortcutHelper.TryCreateStartMenuShortcut();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }


    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()

    {

        OverlayHost.Toggle = dataContext =>

        {

            if (_overlay is { } open)

            {

                _overlay = null;

                open.Close();

                return;

            }

            var overlay = new OverlayWindow { DataContext = dataContext };

            overlay.Closed += (_, _) =>

            {

                if (ReferenceEquals(_overlay, overlay)) _overlay = null;

            };

            _overlay = overlay;

            overlay.Show();

        };

        return AppBuilder.Configure<App>()

            .UsePlatformDetect()

            .WithInterFont()

            .LogToTrace();

    }

}