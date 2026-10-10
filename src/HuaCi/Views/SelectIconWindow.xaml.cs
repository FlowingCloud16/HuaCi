using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HuaCi.Native;

namespace HuaCi.Views;

/// <summary>
/// 划词后出现在鼠标旁的小图标。窗口带 WS_EX_NOACTIVATE，点击时不会抢走原程序的焦点，
/// 这样随后模拟的 Ctrl+C 仍发送到原程序。
/// </summary>
public partial class SelectIconWindow : Window
{
    private readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromSeconds(4) };
    private IntPtr _hwnd;

    public event EventHandler? Clicked;

    public SelectIconWindow()
    {
        InitializeComponent();
        _autoHide.Tick += (_, _) => HideIcon();
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
        if (!IsVisible) Show();

        double scale = ScreenHelper.GetScale(cursor);
        NativeMethods.GetWindowRect(_hwnd, out var r);
        var (x, y) = ScreenHelper.PlaceNear(cursor, r.Width, r.Height, (int)(6 * scale));
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _autoHide.Stop();
        _autoHide.Start();
    }

    public void HideIcon()
    {
        _autoHide.Stop();
        if (IsVisible) Hide();
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Badge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideIcon();
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
