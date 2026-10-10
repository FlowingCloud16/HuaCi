using System.Runtime.InteropServices;

namespace HuaCi.Native;

public enum MouseHookButton { Left, Right, Middle }

public sealed class MouseHookEventArgs(int message, NativeMethods.POINT point, uint time) : EventArgs
{
    public int Message { get; } = message;
    /// <summary>屏幕物理像素坐标。</summary>
    public NativeMethods.POINT Point { get; } = point;
    public uint Time { get; } = time;
}

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）。回调运行在安装钩子的线程（UI 线程）上，
/// 处理函数必须尽量快，否则系统会跳过/移除钩子。
/// </summary>
public sealed class MouseHook : IDisposable
{
    private IntPtr _hook = IntPtr.Zero;
    // 保持委托引用，防止被 GC 回收
    private readonly NativeMethods.LowLevelMouseProc _proc;

    public event EventHandler<MouseHookEventArgs>? MouseEvent;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public MouseHook() => _proc = HookProc;

    public bool Install()
    {
        if (IsInstalled) return true;
        var hMod = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        return IsInstalled;
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg != NativeMethods.WM_MOUSEMOVE) // 忽略移动，降低开销
            {
                try
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    MouseEvent?.Invoke(this, new MouseHookEventArgs(msg, data.pt, data.time));
                }
                catch
                {
                    // 钩子回调中绝不能抛异常
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
