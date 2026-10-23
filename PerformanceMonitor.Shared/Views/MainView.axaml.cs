using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DesktopLayout.IsVisible = !OperatingSystem.IsAndroid();
        MobileLayout.IsVisible = OperatingSystem.IsAndroid();
        MobileNavBorder.AddHandler(PointerPressedEvent, MobileNav_PointerPressed, RoutingStrategies.Tunnel);
    }

    private void MobileNav_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || MobileNavBorder.Bounds.Width <= 0)
            return;

        var column = Math.Clamp((int)(e.GetPosition(MobileNavBorder).X / (MobileNavBorder.Bounds.Width / 6)), 0, 5);
        ICommand command = column switch
        {
            0 => vm.SelectHomeCommand,
            1 => vm.SelectCpuCommand,
            2 => vm.SelectMemoryCommand,
            3 => vm.SelectDiskCommand,
            4 => vm.SelectWifiCommand,
            _ => vm.SelectGpuCommand,
        };
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as MainViewModel)?.Resume();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as MainViewModel)?.Pause();
        base.OnDetachedFromVisualTree(e);
    }
}
