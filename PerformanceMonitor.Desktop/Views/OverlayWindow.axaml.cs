using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace TaskManager.Views;

public partial class OverlayWindow : Window
{
    private int _frameCount;
    private long _fpsStart;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // shortcut: resets to the top-right each launch; persist position if needed.
        // Screens.Primary can be null on X11, so fall back to the first screen.
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen != null)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(
                area.Right - (int)(Width * screen.Scaling) - 16,
                area.Y + 16);
        }

        _frameCount = 0;
        _fpsStart = Stopwatch.GetTimestamp();
        FpsValue.Text = "—";
        RequestAnimationFrame(OnAnimationFrame);
    }

    // shortcut: this is the overlay's render rate, not a game's FPS; game FPS needs a graphics hook.
    private void OnAnimationFrame(TimeSpan _)
    {
        if (!IsVisible) return;

        _frameCount++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _fpsStart) / (double)Stopwatch.Frequency;
        if (elapsed >= 1)
        {
            FpsValue.Text = $"{_frameCount / elapsed:0}";
            _frameCount = 0;
            _fpsStart = now;
        }
        RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        // shortcut: a click can still focus the overlay; add no-activate/click-through interop if that matters.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() != null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
