using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HuaCi.Native;

namespace HuaCi.Views;

/// <summary>
/// 划词后出现在鼠标旁的"翻译"按钮。窗口带 WS_EX_NOACTIVATE，点击时不会抢走原程序的焦点，
/// 这样随后模拟的 Ctrl+C 仍发送到原程序。
/// </summary>
public partial class SelectIconWindow : Window
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Brush NormalBg = Brushes.White;
    private static readonly Brush HoverBg = new SolidColorBrush(Color.FromRgb(0x4D, 0x6B, 0xFE));
    private static readonly Brush NormalFg = new SolidColorBrush(Color.FromRgb(0x3B, 0x4C, 0xCA));

    private readonly DispatcherTimer _autoHide = new() { Interval = Timeout };
    private IntPtr _hwnd;

    public event EventHandler? Clicked;

    /// <summary>超时自动隐藏。</summary>
    public event EventHandler? AutoHidden;

    public SelectIconWindow()
    {
        InitializeComponent();
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); AutoHidden?.Invoke(this, EventArgs.Empty); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.AddExStyle(_hwnd, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        };
    }

    public bool ContainsPoint(NativeMethods.POINT p) =>
        IsVisible && _hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r) && r.Contains(p);

    /// <summary>在鼠标右下方显示（物理像素）。</summary>
    public void ShowAt(NativeMethods.POINT cursor)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, cursor.X, cursor.Y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        SetHover(false);
        Opacity = 0;
        if (!IsVisible) Show();
        UpdateLayout();

        double scale = ScreenHelper.GetScale(cursor);
        NativeMethods.GetWindowRect(_hwnd, out var r);
        // 窗口四周有 8 DIP 阴影边距：让按钮本体出现在光标右下方约 6 DIP 处
        int offset = (int)(-2 * scale);
        var (x, y) = ScreenHelper.PlaceNear(cursor, r.Width, r.Height, offset);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // 淡入 + 轻微放大
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        var grow = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        _autoHide.Stop();
        _autoHide.Start();
    }

    public void HideIcon()
    {
        _autoHide.Stop();
        if (IsVisible) Hide();
    }

    private void SetHover(bool hover)
    {
        Badge.Background = hover ? HoverBg : NormalBg;
        Badge.BorderBrush = hover ? HoverBg : new SolidColorBrush(Color.FromRgb(0xDC, 0xE1, 0xF5));
        Label.Foreground = hover ? Brushes.White : NormalFg;
    }

    private void Badge_MouseEnter(object sender, MouseEventArgs e)
    {
        SetHover(true);
        _autoHide.Stop(); // 鼠标悬停时不自动消失
    }

    private void Badge_MouseLeave(object sender, MouseEventArgs e)
    {
        SetHover(false);
        _autoHide.Start();
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Badge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideIcon();
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
