using System.Windows.Interop;

namespace HuaCi.Native;

/// <summary>
/// 通过 RegisterHotKey 注册多个全局热键（按 id 区分）。使用一个仅消息窗口接收 WM_HOTKEY。
/// 必须在 UI 线程创建和使用。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyGesture> _registered = new();

    /// <summary>参数为热键 id。</summary>
    public event EventHandler<int>? HotkeyPressed;

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

    public bool IsRegistered(int id) => _registered.ContainsKey(id);

    /// <summary>注册热键；失败（通常是被其他程序占用）时返回 false。</summary>
    public bool Register(int id, HotkeyGesture gesture)
    {
        Unregister(id);
        if (!gesture.IsValid) return false;
        bool ok = NativeMethods.RegisterHotKey(_source.Handle, id,
            gesture.NativeModifiers | NativeMethods.MOD_NOREPEAT, gesture.VirtualKey);
        if (ok) _registered[id] = gesture;
        return ok;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.ContainsKey(wParam.ToInt32()))
        {
            handled = true;
            HotkeyPressed?.Invoke(this, wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
