using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuaCi.Native;
using HuaCi.Services;

namespace HuaCi.Views;

/// <summary>
/// 单个显示器上的截图遮罩。显示冻结的屏幕截图并变暗，选区部分保持原亮度。
/// 选区坐标统一用物理像素，由 <see cref="ScreenshotSelector"/> 在所有显示器的遮罩之间共享，
/// 因此可以跨显示器框选，且每块屏幕按各自的 DPI 绘制。
/// </summary>
public partial class ScreenshotOverlay : Window
{
    private readonly ScreenHelper.MonitorInfo _monitor;
    private readonly ScreenshotSelector _owner;
    private IntPtr _hwnd;

    public ScreenshotOverlay(ScreenHelper.MonitorInfo monitor, BitmapSource image, ScreenshotSelector owner, bool showHint)
    {
        InitializeComponent();
        _monitor = monitor;
        _owner = owner;
        Shot.Source = image;
        Hint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.AddExStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => Render(_owner.Selection);
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonDown += (_, e) => { e.Handled = true; _owner.Cancel(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; _owner.Cancel(); }
        };
        Deactivated += (_, _) => _owner.OnOverlayDeactivated();
    }

    /// <summary>铺满对应显示器（物理像素）。</summary>
    public void ShowOnMonitor()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var b = _monitor.Bounds;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, NativeMethods.SWP_NOACTIVATE);
        Show();
        // Show 之后 WPF 可能按自己的尺寸调整一次，再定位一次确保铺满
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, NativeMethods.SWP_NOACTIVATE);
        Render(null);
    }

    public bool ContainsCursor(NativeMethods.POINT p) => _monitor.Bounds.Contains(p);

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CaptureMouse();
        _owner.Begin(ScreenHelper.GetCursorPos());
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_owner.IsDragging) _owner.Update(ScreenHelper.GetCursorPos());
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ReleaseMouseCapture();
        _owner.End(ScreenHelper.GetCursorPos());
    }

    /// <summary>按当前选区重绘（selection 为物理像素坐标，null 表示无选区）。</summary>
    public void Render(NativeMethods.RECT? selection)
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var b = _monitor.Bounds;
        double sx = w / Math.Max(1, b.Width), sy = h / Math.Max(1, b.Height);
        var full = new RectangleGeometry(new Rect(0, 0, w, h));

        if (selection is not { } s || s.Width <= 0 || s.Height <= 0)
        {
            Dim.Data = full;
            SelBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        Hint.Visibility = Visibility.Collapsed;
        var rect = new Rect((s.Left - b.Left) * sx, (s.Top - b.Top) * sy, s.Width * sx, s.Height * sy);
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(rect));

        var visible = Rect.Intersect(rect, new Rect(0, 0, w, h));
        if (visible.IsEmpty)
        {
            SelBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        SelBorder.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelBorder, rect.X);
        System.Windows.Controls.Canvas.SetTop(SelBorder, rect.Y);
        SelBorder.Width = rect.Width;
        SelBorder.Height = rect.Height;

        // 尺寸标签只显示在选区左上角所在的屏幕
        if (b.Contains(new NativeMethods.POINT(s.Left, s.Top)))
        {
            SizeText.Text = $"{s.Width} × {s.Height}";
            SizeLabel.Visibility = Visibility.Visible;
            double ly = rect.Y - 24 >= 0 ? rect.Y - 24 : rect.Y + 4;
            System.Windows.Controls.Canvas.SetLeft(SizeLabel, rect.X);
            System.Windows.Controls.Canvas.SetTop(SizeLabel, ly);
        }
        else
        {
            SizeLabel.Visibility = Visibility.Collapsed;
        }
    }
}
