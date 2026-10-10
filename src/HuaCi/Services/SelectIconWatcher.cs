using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Views;
using static HuaCi.Native.NativeMethods;

namespace HuaCi.Services;

/// <summary>
/// 可选功能：通过低级鼠标钩子检测"拖动选择"或"双击选词"，在鼠标旁显示翻译小图标。
/// </summary>
public sealed class SelectIconWatcher : IDisposable
{
    private readonly MouseHook _hook = new();
    private readonly SelectIconWindow _icon = new();
    private readonly Dispatcher _dispatcher;

    private POINT _downPt;
    private uint _downTime;
    private POINT _lastUpPt;
    private uint _lastUpTime;
    private bool _downOnIcon;

    /// <summary>用户点击了小图标。</summary>
    public event EventHandler? IconClicked;

    public SelectIconWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hook.MouseEvent += OnMouse;
        _icon.Clicked += (_, _) => IconClicked?.Invoke(this, EventArgs.Empty);
    }

    public bool Enabled => _hook.IsInstalled;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!_hook.Install()) Log.Warn("安装鼠标钩子失败");
        }
        else
        {
            _hook.Uninstall();
            _icon.HideIcon();
        }
    }

    private void OnMouse(object? sender, MouseHookEventArgs e)
    {
        switch (e.Message)
        {
            case WM_LBUTTONDOWN:
                _downOnIcon = _icon.ContainsPoint(e.Point);
                _downPt = e.Point;
                _downTime = e.Time;
                if (!_downOnIcon) _dispatcher.BeginInvoke(_icon.HideIcon);
                break;

            case WM_LBUTTONUP:
                if (_downOnIcon || _icon.ContainsPoint(e.Point)) break;
                var up = e.Point;
                var time = e.Time;
                bool isDrag = Distance(_downPt, up) >= DragThreshold(up) && time - _downTime >= 80;
                bool isDoubleClick = time - _lastUpTime <= GetDoubleClickTime()
                    && Math.Abs(up.X - _lastUpPt.X) <= GetSystemMetrics(SM_CXDOUBLECLK)
                    && Math.Abs(up.Y - _lastUpPt.Y) <= GetSystemMetrics(SM_CYDOUBLECLK);
                _lastUpPt = up;
                _lastUpTime = isDoubleClick ? 0 : time; // 避免三击被当成两次双击
                if (isDrag || isDoubleClick)
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () => MaybeShow(up));
                break;

            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_MOUSEWHEEL:
                _dispatcher.BeginInvoke(_icon.HideIcon);
                break;
        }
    }

    private void MaybeShow(POINT pt)
    {
        // 在本程序自己的窗口里拖动（如弹窗、设置）不显示
        if (ScreenHelper.IsForegroundOurs()) return;
        _icon.ShowAt(pt);
    }

    private static double Distance(POINT a, POINT b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double DragThreshold(POINT p) => 8 * ScreenHelper.GetScale(p);

    public void Dispose()
    {
        _hook.Dispose();
        _icon.Close();
    }
}
