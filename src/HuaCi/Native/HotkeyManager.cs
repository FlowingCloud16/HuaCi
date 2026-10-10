using System.Windows.Interop;

namespace HuaCi.Native;

/// <summary>
/// 通过 RegisterHotKey 注册全局热键。使用一个仅消息窗口接收 WM_HOTKEY。
/// 必须在 UI 线程创建和使用。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x4843; // "HC"
    private readonly HwndSource _source;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public HotkeyGesture? Current { get; private set; }

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("HuaCiHotkeyWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>注册热键；失败（通常是被其他程序占用）时返回 false。</summary>
    public bool Register(HotkeyGesture gesture)
    {
        Unregister();
        if (!gesture.IsValid) return false;
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId,
            gesture.NativeModifiers | NativeMethods.MOD_NOREPEAT, gesture.VirtualKey);
        Current = _registered ? gesture : null;
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
