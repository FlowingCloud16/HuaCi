namespace HuaCi.Native;

/// <summary>多显示器 / 多 DPI 定位辅助。所有坐标为物理像素。</summary>
public static class ScreenHelper
{
    public static NativeMethods.POINT GetCursorPos()
    {
        NativeMethods.GetCursorPos(out var p);
        return p;
    }

    /// <summary>获取某点所在显示器的工作区（排除任务栏）。</summary>
    public static NativeMethods.RECT GetWorkArea(NativeMethods.POINT pt)
    {
        var mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (mon != IntPtr.Zero && NativeMethods.GetMonitorInfo(mon, ref info))
            return info.rcWork;
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>某点所在显示器的缩放比例（1.0 = 96 DPI）。</summary>
    public static double GetScale(NativeMethods.POINT pt)
    {
        try
        {
            var mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (NativeMethods.GetDpiForMonitor(mon, 0 /*MDT_EFFECTIVE_DPI*/, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    /// <summary>
    /// 计算窗口位置：优先放在鼠标右下方，放不下则翻到左侧/上方，最后夹紧在工作区内。
    /// </summary>
    public static (int X, int Y) PlaceNear(NativeMethods.POINT anchor, int width, int height, int offset)
    {
        var wa = GetWorkArea(anchor);
        int x = anchor.X + offset;
        int y = anchor.Y + offset;
        if (x + width > wa.Right) x = anchor.X - offset - width;
        if (y + height > wa.Bottom) y = anchor.Y - offset - height;
        return Clamp(wa, x, y, width, height);
    }

    public static (int X, int Y) Clamp(NativeMethods.RECT wa, int x, int y, int width, int height)
    {
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - width));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - height));
        return (x, y);
    }

    /// <summary>判断前台窗口是否属于本进程。</summary>
    public static bool IsForegroundOurs()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>显示器信息（物理像素）。</summary>
    public sealed record MonitorInfo(IntPtr Handle, NativeMethods.RECT Bounds, NativeMethods.RECT WorkArea, double Scale);

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref NativeMethods.RECT r, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(h, ref info);
            var center = new NativeMethods.POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);
            list.Add(new MonitorInfo(h, info.rcMonitor, info.rcWork, GetScale(center)));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
