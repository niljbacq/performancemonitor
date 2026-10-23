using System;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware.Display;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using TaskManager.Providers;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace TaskManager.Droid;

// A native floating window hosted by MonitorForegroundService so it keeps
// updating while the Avalonia activity is in the background.
internal sealed class AndroidOverlay : IDisposable
{
    private readonly Context _context;
    private readonly IWindowManager _windowManager;
    private readonly LinearLayout _root;
    private readonly WindowManagerLayoutParams _layoutParams;
    private readonly Handler _handler;
    private readonly SamplingService _sampling;
    private readonly MetricRow _cpu;
    private readonly MetricRow _memory;
    private readonly MetricRow _gpu;
    private readonly MetricRow _gpuTemp;
    private readonly MetricRow _fps;
    private bool _disposed;

    public event Action? CloseRequested;

    public AndroidOverlay(Context context)
    {
        _context = context;
        _windowManager = context.GetSystemService(Context.WindowService)?.JavaCast<IWindowManager>()
            ?? throw new InvalidOperationException("Window service unavailable.");
        _handler = new Handler(Looper.MainLooper!);
        _sampling = SamplingService.Current ?? SamplingService.StartDefault();

        _root = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _root.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#252526"));
        background.SetCornerRadius(Dp(8));
        background.SetStroke(Dp(1), Color.ParseColor("#333333"));
        _root.Background = background;

        _root.AddView(BuildHeader());
        _cpu = AddBarRow("CPU");
        _memory = AddBarRow("Memory");
        _gpu = AddBarRow("GPU");
        _gpuTemp = AddTextRow("GPU temp");
        _fps = AddTextRow("FPS");

        _layoutParams = new WindowManagerLayoutParams(
            Dp(220),
            ViewGroup.LayoutParams.WrapContent,
            OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.End,
            X = Dp(12),
            Y = Dp(64)
        };

        _windowManager.AddView(_root, _layoutParams);
        _sampling.SampleCollected += OnSample;
    }

    private View BuildHeader()
    {
        var header = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);

        var title = new TextView(_context)
        {
            Text = "Performance",
            TextSize = 11,
            Gravity = GravityFlags.CenterVertical
        };
        title.SetTextColor(Color.ParseColor("#888888"));
        title.SetOnTouchListener(new DragListener(this));
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var close = new TextView(_context)
        {
            Text = "×",
            TextSize = 18,
            Gravity = GravityFlags.Center
        };
        close.SetTextColor(Color.ParseColor("#AAAAAA"));
        close.SetPadding(Dp(6), 0, Dp(6), 0);
        close.Click += (_, _) => CloseRequested?.Invoke();
        header.AddView(close, new LinearLayout.LayoutParams(Dp(28), Dp(28)));

        return header;
    }

    private MetricRow AddBarRow(string label)
    {
        var row = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        row.SetPadding(0, Dp(6), 0, 0);

        var line = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        var name = new TextView(_context) { Text = label, TextSize = 12 };
        name.SetTextColor(Color.ParseColor("#CCCCCC"));
        var value = new TextView(_context) { TextSize = 12 };
        value.SetTextColor(Color.White);
        line.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        line.AddView(value, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        row.AddView(line);

        var bar = new ProgressBar(_context, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 100,
            Indeterminate = false
        };
        bar.ProgressTintList = ColorStateList.ValueOf(Color.ParseColor("#0078D4"));
        bar.ProgressBackgroundTintList = ColorStateList.ValueOf(Color.ParseColor("#333333"));
        bar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(4));
        row.AddView(bar);

        _root.AddView(row);
        return new MetricRow(row, value, bar);
    }

    private MetricRow AddTextRow(string label)
    {
        var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Dp(6), 0, 0);
        var name = new TextView(_context) { Text = label, TextSize = 12 };
        name.SetTextColor(Color.ParseColor("#CCCCCC"));
        var value = new TextView(_context) { TextSize = 12 };
        value.SetTextColor(Color.White);
        row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(value, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _root.AddView(row);
        return new MetricRow(row, value, null);
    }

    private void OnSample(PerfSample sample)
    {
        if (_disposed) return;
        _handler.Post(() => Apply(sample));
    }

    private void Apply(PerfSample sample)
    {
        if (_disposed) return;
        SetBar(_cpu, sample.CpuPercent);
        SetBar(_memory, sample.MemoryPercent);
        SetBar(_gpu, sample.GpuUtilization);
        SetText(_gpuTemp, sample.GpuTemperatureC is double t ? $"{t:0} °C" : null);

        // shortcut: display mode rate, not the overlay's rendered frame rate; use Choreographer
        // frame counts if presented frames must be measured exactly.
        var refresh = _context.GetSystemService(Context.DisplayService)
            ?.JavaCast<DisplayManager>()?.GetDisplay(Display.DefaultDisplay)?.RefreshRate;
        SetText(_fps, refresh is float hz && hz > 0 ? $"{hz:0}" : null);
    }

    private static void SetBar(MetricRow row, double? value)
    {
        if (value is double v)
        {
            row.Row.Visibility = ViewStates.Visible;
            row.Value.Text = $"{v:0}%";
            if (row.Bar != null) row.Bar.Progress = (int)Math.Clamp(v, 0, 100);
        }
        else
        {
            row.Row.Visibility = ViewStates.Gone;
        }
    }

    private static void SetText(MetricRow row, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            row.Row.Visibility = ViewStates.Gone;
        }
        else
        {
            row.Row.Visibility = ViewStates.Visible;
            row.Value.Text = text;
        }
    }

    private int Dp(float value) => (int)(value * _context.Resources!.DisplayMetrics!.Density + 0.5f);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampling.SampleCollected -= OnSample;
        try { _windowManager.RemoveView(_root); } catch { }
    }

    private sealed class MetricRow
    {
        public MetricRow(LinearLayout row, TextView value, ProgressBar? bar)
        {
            Row = row;
            Value = value;
            Bar = bar;
        }

        public LinearLayout Row { get; }
        public TextView Value { get; }
        public ProgressBar? Bar { get; }
    }

    private sealed class DragListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidOverlay _owner;
        private float _downX;
        private float _downY;
        private int _startX;
        private int _startY;

        public DragListener(AndroidOverlay owner) => _owner = owner;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null) return false;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _downX = e.RawX;
                    _downY = e.RawY;
                    _startX = _owner._layoutParams.X;
                    _startY = _owner._layoutParams.Y;
                    return true;
                case MotionEventActions.Move:
                    _owner._layoutParams.X = _startX - (int)(e.RawX - _downX);
                    _owner._layoutParams.Y = _startY + (int)(e.RawY - _downY);
                    _owner._windowManager.UpdateViewLayout(_owner._root, _owner._layoutParams);
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    return true;
                default:
                    return false;
            }
        }
    }
}
