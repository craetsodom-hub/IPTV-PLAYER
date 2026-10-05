using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IptvPlayer.App.Controls;

/// <summary>
/// A small, embedded WPF visual with its own animation dispatcher. Catalog
/// binding and layout on the window's dispatcher cannot stop its animation clock.
/// All indicators share one background STA thread; no native window is created.
/// </summary>
public class SmoothLoadingIndicator : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(SmoothLoadingIndicator),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, OnAppearanceChanged));

    private static readonly Lazy<Task<Dispatcher>> AnimationDispatcher = new(CreateAnimationDispatcher);
    private readonly HostVisual _host = new();
    // Only the animation dispatcher accesses the session.
    private AnimationSession? _session;
    private int _requestVersion;
#if PLAYBACK_DIAGNOSTICS
    internal Task<LoadingAnimationSnapshot> ReadAnimationSnapshotAsync() => ReadSnapshotAsync();
    internal async Task ResetAnimationMetricsAsync()
    {
        var dispatcher = await AnimationDispatcher.Value;
        await dispatcher.InvokeAsync(() => _session?.ResetMetrics());
    }
    internal sealed record LoadingAnimationSnapshot(bool Active, double Position, int ThreadId, long Frames, double MaxFrameGapMs);
#endif
    protected virtual bool IsBar => false;

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public SmoothLoadingIndicator()
    {
        IsHitTestVisible = false;
        AddVisualChild(_host);
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        SizeChanged += (_, _) => UpdateAnimation();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _host : throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size availableSize)
        => new(Math.Min(52, availableSize.Width), Math.Min(52, availableSize.Height));

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((SmoothLoadingIndicator)sender).UpdateAnimation();

    private async void UpdateAnimation()
    {
        var version = Interlocked.Increment(ref _requestVersion);
        var loaded = IsLoaded;
        var active = loaded && IsVisible && ActualWidth > 0 && ActualHeight > 0;
        var size = RenderSize;
        var color = Foreground is SolidColorBrush brush ? brush.Color : Colors.Goldenrod;
        var isBar = IsBar;
        // Hidden templates must not start a thread or establish a visual target.
        if (!active && !AnimationDispatcher.IsValueCreated) return;

        var dispatcher = await AnimationDispatcher.Value;
        await dispatcher.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _requestVersion)) return;
            if (!loaded)
            {
                _session?.Dispose();
                _session = null;
                return;
            }
            if (active) _session ??= new AnimationSession(_host, isBar);
            _session?.Update(size, color, active);
        }, DispatcherPriority.Normal);
    }

    private static Task<Dispatcher> CreateAnimationDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Whose IPTV loading animation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

#if PLAYBACK_DIAGNOSTICS
    private async Task<LoadingAnimationSnapshot> ReadSnapshotAsync()
    {
        if (!AnimationDispatcher.IsValueCreated) return new(false, 0, 0, 0, 0);
        var dispatcher = await AnimationDispatcher.Value;
        return await dispatcher.InvokeAsync(() => new LoadingAnimationSnapshot(
            _session?.Active ?? false, _session?.Position ?? 0,
            Environment.CurrentManagedThreadId, _session?.Frames ?? 0, _session?.MaxFrameGapMs ?? 0));
    }
#endif

    private sealed class AnimationSession : IDisposable
    {
        private readonly VisualTarget _target;
        private readonly ContainerVisual _root = new();
        private readonly DrawingVisual _track = new();
        private readonly DrawingVisual _arc = new();
        private readonly RotateTransform _rotation = new();
        private readonly TranslateTransform _translation = new();
        private readonly bool _isBar;
        private Size _size;
        private Color _color;
        public bool Active { get; private set; }
#if PLAYBACK_DIAGNOSTICS
        public double Position => _isBar ? _translation.X : _rotation.Angle;
        public long Frames { get; private set; }
        public double MaxFrameGapMs { get; private set; }
        private long _previousFrameTick;
        public void ResetMetrics()
        {
            Frames = 0;
            _previousFrameTick = 0;
            MaxFrameGapMs = 0;
        }
        private void OnRendering(object? sender, EventArgs e)
        {
            var tick = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_previousFrameTick != 0)
                MaxFrameGapMs = Math.Max(MaxFrameGapMs,
                    System.Diagnostics.Stopwatch.GetElapsedTime(_previousFrameTick, tick).TotalMilliseconds);
            _previousFrameTick = tick;
            Frames++;
        }
#endif

        public AnimationSession(HostVisual host, bool isBar)
        {
            _isBar = isBar;
            _root.Children.Add(_track);
            _root.Children.Add(_arc);
            _arc.Transform = isBar ? _translation : _rotation;
            _target = new VisualTarget(host) { RootVisual = _root };
        }

        public void Update(Size size, Color color, bool active)
        {
            if (active && (size != _size || color != _color))
            {
                _size = size;
                _color = color;
                if (_isBar) DrawBar(); else DrawRing();
            }
            if (active == Active) return;
            Active = active;
            if (active)
            {
                if (_isBar)
                {
                    var sweep = new DoubleAnimation(-120, _size.Width + 4, TimeSpan.FromSeconds(1))
                        { RepeatBehavior = RepeatBehavior.Forever };
                    _translation.BeginAnimation(TranslateTransform.XProperty, sweep);
                }
                else
                {
                    var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
                        { RepeatBehavior = RepeatBehavior.Forever };
                    _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
                }
#if PLAYBACK_DIAGNOSTICS
                _previousFrameTick = 0;
                MaxFrameGapMs = 0;
                CompositionTarget.Rendering += OnRendering;
#endif
            }
            else
            {
                _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                _translation.BeginAnimation(TranslateTransform.XProperty, null);
#if PLAYBACK_DIAGNOSTICS
                CompositionTarget.Rendering -= OnRendering;
#endif
            }
        }

        private void DrawBar()
        {
            var bounds = new Rect(_size);
            var radius = Math.Min(2, _size.Height / 2);
            var trackBrush = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
            trackBrush.Freeze();
            var borderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
            borderBrush.Freeze();
            var border = new Pen(borderBrush, 1);
            border.Freeze();
            using (var drawing = _track.RenderOpen())
                drawing.DrawRoundedRectangle(trackBrush, border,
                    new Rect(0.5, 0.5, Math.Max(0, _size.Width - 1), Math.Max(0, _size.Height - 1)), radius, radius);
            var gradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 78, 162, 255), 0),
                new(_color, 0.25), new(Colors.White, 0.5),
                new(_color, 0.75), new(Color.FromArgb(0, 78, 162, 255), 1)
            }, new Point(0, 0), new Point(1, 0));
            gradient.Freeze();
            using (var drawing = _arc.RenderOpen())
                drawing.DrawRoundedRectangle(gradient, null, new Rect(0, 0, 120, _size.Height), radius, radius);
            var clip = new RectangleGeometry(bounds);
            clip.Freeze();
            _root.Clip = clip;
        }

        private void DrawRing()
        {
            var center = new Point(_size.Width / 2, _size.Height / 2);
            var thickness = Math.Max(2, Math.Min(_size.Width, _size.Height) * 0.067);
            var radius = Math.Max(0, Math.Min(_size.Width, _size.Height) / 2 - thickness);
            var brush = new SolidColorBrush(_color);
            brush.Freeze();
            var trackBrush = new SolidColorBrush(Color.FromArgb((byte)(_color.A * 0.18), _color.R, _color.G, _color.B));
            trackBrush.Freeze();
            var trackPen = new Pen(trackBrush, thickness);
            trackPen.Freeze();
            var arcPen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            arcPen.Freeze();
            using (var drawing = _track.RenderOpen())
            {
                // Keep cross-dispatcher visual bounds stable throughout rotation.
                drawing.DrawRectangle(Brushes.Transparent, null, new Rect(_size));
                drawing.DrawEllipse(null, trackPen, center, radius, radius);
            }
            var clip = new RectangleGeometry(new Rect(_size));
            clip.Freeze();
            _root.Clip = clip;
            var arc = new StreamGeometry();
            using (var geometry = arc.Open())
            {
                geometry.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                geometry.ArcTo(new Point(center.X - radius, center.Y), new Size(radius, radius), 0,
                    true, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            using (var drawing = _arc.RenderOpen()) drawing.DrawGeometry(null, arcPen, arc);
            _rotation.CenterX = center.X;
            _rotation.CenterY = center.Y;
        }

        public void Dispose()
        {
            Update(_size, _color, false);
            _target.Dispose();
        }
    }
}
