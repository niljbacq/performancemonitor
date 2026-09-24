using Avalonia;

using System;


namespace TaskManager;


internal class Program

{

    // Initialization code. Don't use any Avalonia, third-party APIs or any

    // SynchronizationContext-reliant code before BuildAvaloniaApp() has been called!

    [STAThread]

    public static void Main(string[] args) => BuildAvaloniaApp()

        .StartWithClassicDesktopLifetime(args);


    // Avalonia configuration, don't remove; also used by visual designer.

    public static AppBuilder BuildAvaloniaApp()

        => AppBuilder.Configure<App>()

            .UsePlatformDetect()

            .WithInterFont()

            .LogToTrace();

}

