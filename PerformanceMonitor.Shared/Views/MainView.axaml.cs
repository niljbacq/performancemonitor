using Avalonia;
using Avalonia.Controls;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
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
