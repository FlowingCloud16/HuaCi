using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Views;
using static HuaCi.Native.NativeMethods;

namespace HuaCi.Services;

/// <summary>
/// 划词按钮（主要交互方式）：通过低级鼠标钩子检测"拖动选择"或"双击选词"，在鼠标旁显示翻译按钮。
/// 为减少误判，以下情况不显示：
/// - 拖动距离太短 / 时间太短；
/// - 按下时鼠标在标题栏、边框、滚动条等非客户区（WM_NCHITTEST ≠ HTCLIENT）；
/// - 拖动过程中窗口被移动或缩放；
/// - 桌面、任务栏、本程序自己的窗口；
/// - 拖动：按下时是普通箭头 / 手形 / 缩放光标（文本区域通常是 I 形光标）；
/// - 双击：必须是 I 形光标（避免双击打开文件夹 / 文件时误触发）；
/// - 列表 / 树形 / 工具栏等控件（文件列表、资源管理器项目视图），以及资源管理器中除输入框外的区域。
/// 按钮在点击别处、按键、滚轮、切换窗口或超时后自动消失。
/// </summary>
public sealed class SelectIconWatcher : IDisposable
{
    private const double MinDragDip = 12;      // 最小拖动距离（DIP）
    private const uint MinDragMs = 120;        // 最短拖动时长

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW",                                   // 桌面
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",              // 任务栏
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",                           // 开始菜单 / 搜索等系统界面
        "XamlExplorerHostIslandWindow",                         // Alt+Tab / 任务视图
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS",  // 终端：Ctrl+C 是中断
    };

    /// <summary>鼠标下的控件属于这些类时不显示（文件列表、树、表头、工具栏、状态栏…）。</summary>
    private static readonly HashSet<string> IgnoredControlClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SysListView32", "SysTreeView32", "DirectUIHWND", "SysHeader32", "ToolbarWindow32",
        "msctls_statusbar32", "ReBarWindow32", "SHELLDLL_DefView", "NamespaceTreeControl",
        "Breadcrumb Parent", "UIRibbonWorkPane", "NetUIHWND",
    };

    /// <summary>资源管理器 / 文件对话框的窗口类：只在其中的文本输入框里允许显示。</summary>
    private static readonly HashSet<string> ExplorerRootClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass", "ExploreWClass", "#32770",
    };

    private static readonly int[] NonTextCursors =
        [IDC_ARROW, IDC_HAND, IDC_SIZEALL, IDC_SIZENWSE, IDC_SIZENESW, IDC_SIZEWE, IDC_SIZENS];

    private readonly MouseHook _mouse = new();
    private readonly KeyboardHook _keyboard = new();
    private readonly SelectIconWindow _icon = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _foregroundWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<IntPtr> _nonTextCursorHandles;

    // 按下时的状态（在钩子线程=UI 线程中记录，只调用不发送消息的轻量 API）
    private POINT _downPt;
    private uint _downTime;
    private IntPtr _downRoot;
    private RECT _downRect;
    private bool _downCursorIsText;
    private bool _downCursorIsIBeam;
    private readonly IntPtr _ibeamCursor = StandardCursor(IDC_IBEAM);
    private bool _downOnIcon;

    private POINT _lastUpPt;
    private uint _lastUpTime;
    private IntPtr _iconForeground;

    /// <summary>用户点击了划词按钮。</summary>
    public event EventHandler? IconClicked;

    public SelectIconWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _nonTextCursorHandles = NonTextCursors.Select(StandardCursor).Where(h => h != IntPtr.Zero).ToHashSet();
        _mouse.MouseEvent += OnMouse;
        _keyboard.KeyDown += (_, _) => _dispatcher.BeginInvoke(HideIcon);
        _icon.Clicked += (_, _) =>
        {
            HideIcon();
            IconClicked?.Invoke(this, EventArgs.Empty);
        };
        _icon.AutoHidden += (_, _) => HideIcon();
        _foregroundWatch.Tick += (_, _) =>
        {
            // 用户切换了窗口（Alt+Tab、任务栏等）→ 隐藏
            if (GetForegroundWindow() != _iconForeground) HideIcon();
        };
    }

    public bool Enabled => _mouse.IsInstalled;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!_mouse.Install()) Log.Warn("安装鼠标钩子失败");
        }
        else
        {
            _mouse.Uninstall();
            HideIcon();
        }
    }

    private void OnMouse(object? sender, MouseHookEventArgs e)
    {
        switch (e.Message)
        {
            case WM_LBUTTONDOWN:
                _downOnIcon = _icon.ContainsPoint(e.Point);
                if (_downOnIcon) break;
                _downPt = e.Point;
                _downTime = e.Time;
                _downRoot = GetAncestor(WindowFromPoint(e.Point), GA_ROOT);
                GetWindowRect(_downRoot, out _downRect);
                var cur = CurrentCursor();
                _downCursorIsText = cur == IntPtr.Zero || !_nonTextCursorHandles.Contains(cur);
                _downCursorIsIBeam = cur != IntPtr.Zero && cur == _ibeamCursor;
                if (_icon.IsVisible) _dispatcher.BeginInvoke(HideIcon);
                break;

            case WM_LBUTTONUP:
                if (_downOnIcon || _icon.ContainsPoint(e.Point)) break;
                var up = e.Point;
                var time = e.Time;
                double scale = ScreenHelper.GetScale(up);
                bool isDrag = Distance(_downPt, up) >= MinDragDip * scale && time - _downTime >= MinDragMs;
                bool isDoubleClick = time - _lastUpTime <= GetDoubleClickTime()
                    && Math.Abs(up.X - _lastUpPt.X) <= GetSystemMetrics(SM_CXDOUBLECLK)
                    && Math.Abs(up.Y - _lastUpPt.Y) <= GetSystemMetrics(SM_CYDOUBLECLK);
                _lastUpPt = up;
                _lastUpTime = isDoubleClick ? 0 : time; // 避免三击被当成两次双击

                // 拖动要求按下时是文本类光标；双击必须是 I 形光标（双击打开文件夹时是箭头）
                if ((isDrag && _downCursorIsText) || (isDoubleClick && _downCursorIsIBeam))
                {
                    var root = _downRoot;
                    var rect = _downRect;
                    var downPt = _downPt;
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = MaybeShowAsync(up, downPt, root, rect));
                }
                break;

            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_MOUSEWHEEL:
                if (_icon.IsVisible) _dispatcher.BeginInvoke(HideIcon);
                break;
        }
    }

    /// <summary>在 UI 线程上做较"重"的检查（可能跨进程发消息），然后决定是否显示。</summary>
    private async Task MaybeShowAsync(POINT up, POINT downPt, IntPtr root, RECT downRect)
    {
        try
        {
            if (root == IntPtr.Zero || ScreenHelper.IsForegroundOurs()) return;

            // 拖动期间窗口被移动 / 缩放 → 是在拖窗口，不是选文字
            if (GetWindowRect(root, out var nowRect) &&
                (nowRect.Left != downRect.Left || nowRect.Top != downRect.Top ||
                 nowRect.Width != downRect.Width || nowRect.Height != downRect.Height))
                return;

            var rootClass = ClassNameOf(root);
            if (IgnoredClasses.Contains(rootClass)) return;

            var target = WindowFromPoint(downPt);
            var targetClass = target != IntPtr.Zero ? ClassNameOf(target) : "";
            if (IgnoredControlClasses.Contains(targetClass)) return;

            // 资源管理器 / 文件对话框：只允许在文本输入框（地址栏编辑状态、搜索框、重命名框）中显示
            if (ExplorerRootClasses.Contains(rootClass) || IsExplorerProcess(root))
            {
                bool isEditClass = targetClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                                   || targetClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
                if (!isEditClass && !await IsTextElementAtAsync(downPt)) return;
            }

            // 按下位置不在客户区（标题栏、边框、滚动条、菜单…）
            if (target != IntPtr.Zero &&
                SendMessageTimeout(target, WM_NCHITTEST, IntPtr.Zero, MakeLParam(downPt.X, downPt.Y),
                    SMTO_ABORTIFHUNG, 100, out var hit) != IntPtr.Zero &&
                hit.ToInt64() != HTCLIENT && hit.ToInt64() != -1 /* HTTRANSPARENT */)
                return;

            // 等待期间用户可能已经点了别处
            if (GetForegroundWindow() != root && GetAncestor(GetForegroundWindow(), GA_ROOT) != root) return;

            _iconForeground = GetForegroundWindow();
            _icon.ShowAt(up);
            _keyboard.Install();
            _foregroundWatch.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("划词检测出错：" + ex.Message);
        }
    }

    private static bool IsExplorerProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用 UI Automation 判断某点下的元素是否是文本输入 / 文档（Edit、Document 或支持 TextPattern）。
    /// 只用于资源管理器这类容易误判的窗口；放在后台线程并限时 300ms，超时视为否。
    /// </summary>
    private static async Task<bool> IsTextElementAtAsync(POINT pt)
    {
        var task = Task.Run(() =>
        {
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                if (el is null) return false;
                var type = el.Current.ControlType;
                if (type == System.Windows.Automation.ControlType.Edit || type == System.Windows.Automation.ControlType.Document)
                    return true;
                return el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out _);
            }
            catch
            {
                return false;
            }
        });
        var done = await Task.WhenAny(task, Task.Delay(300));
        return done == task && task.Result;
    }

    public void HideIcon()
    {
        _foregroundWatch.Stop();
        _keyboard.Uninstall();
        _icon.HideIcon();
    }

    private static double Distance(POINT a, POINT b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Dispose()
    {
        HideIcon();
        _mouse.Dispose();
        _keyboard.Dispose();
        _icon.Close();
    }
}
