using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using HuaCi.Native;
using static HuaCi.Native.NativeMethods;

namespace HuaCi.Services;

/// <summary>
/// 获取当前前台程序中选中的文字：
/// 1. 备份剪贴板 → 2. 模拟 Ctrl+C → 3. 读取剪贴板文字 → 4. 还原剪贴板；
/// 若剪贴板未变化，则尝试 UI Automation 的 TextPattern 作为后备。
/// 必须在 UI（STA）线程调用。
/// </summary>
public static class SelectionCapture
{
    public static async Task<string?> CaptureAsync(CancellationToken ct = default)
    {
        // 用户按热键时手指可能还按着 Ctrl/Alt/Shift，先等它们松开，否则目标程序收到的会是 Ctrl+Alt+C 之类
        await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(600), ct);

        var backup = ClipboardBackup.Take();
        uint seqBefore = GetClipboardSequenceNumber();

        SendCtrlC();

        // 等待剪贴板序号变化
        bool changed = false;
        for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++) // 最多约 600ms
        {
            await Task.Delay(15, ct);
            if (GetClipboardSequenceNumber() != seqBefore) { changed = true; break; }
        }

        string? text = null;
        if (changed)
        {
            // 有些程序会分多次写入剪贴板，稍等一下再读
            await Task.Delay(40, ct);
            text = await RetryAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            await RetryAsync(() => { backup.Restore(); return true; });
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // 后备：UI Automation（部分原生/UWP 程序不响应模拟的 Ctrl+C，但支持 TextPattern）
            text = await TryUiAutomationAsync(TimeSpan.FromMilliseconds(800));
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static async Task WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AnyModifierDown() && DateTime.UtcNow < deadline)
            await Task.Delay(15, ct);

        if (AnyModifierDown())
        {
            // 用户一直按着：合成按键抬起。先发送一个未分配键，防止单独松开 Alt 时激活目标程序的菜单栏
            var inputs = new List<INPUT> { Key(VK_UNASSIGNED, false), Key(VK_UNASSIGNED, true) };
            foreach (var vk in new[] { VK_MENU, VK_SHIFT, VK_CONTROL, VK_LWIN, VK_RWIN })
                if (IsDown(vk)) inputs.Add(Key(vk, true));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            await Task.Delay(20, ct);
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool AnyModifierDown() =>
        IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_C, false),
            Key(VK_C, true),
            Key(VK_CONTROL, true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Warn($"SendInput 只发送了 {sent}/{inputs.Length} 个事件（目标窗口可能以管理员权限运行）。错误码 {Marshal.GetLastWin32Error()}");
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    /// <summary>剪贴板常被其他进程短暂占用（COMException CLIPBRD_E_CANT_OPEN），需要重试。</summary>
    private static async Task<T?> RetryAsync<T>(Func<T?> action, int attempts = 8)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (COMException) { await Task.Delay(25); }
            catch (ExternalException) { await Task.Delay(25); }
        }
        Log.Warn("剪贴板访问多次失败");
        return default;
    }

    private static async Task<string?> TryUiAutomationAsync(TimeSpan timeout)
    {
        // UIA 调用可能阻塞，放到后台线程并加超时
        var task = Task.Run(() =>
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el is null) return null;
                // 不对本进程的窗口使用 UIA，避免死锁
                if (el.Current.ProcessId == Environment.ProcessId) return null;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern tp)
                {
                    var ranges = tp.GetSelection();
                    var parts = ranges.Select(r => r.GetText(4000)).Where(s => !string.IsNullOrEmpty(s));
                    var s = string.Join(Environment.NewLine, parts);
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
            }
            catch (Exception ex)
            {
                Log.Info("UIA 获取选中文字失败：" + ex.Message);
            }
            return (string?)null;
        });
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? task.Result : null;
    }

    /// <summary>剪贴板内容备份（尽量保留所有格式）。</summary>
    private sealed class ClipboardBackup
    {
        private readonly DataObject? _data;
        private readonly bool _wasEmpty;

        private ClipboardBackup(DataObject? data, bool wasEmpty) { _data = data; _wasEmpty = wasEmpty; }

        public static ClipboardBackup Take()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var src = Clipboard.GetDataObject();
                    if (src is null) return new ClipboardBackup(null, true);
                    var formats = src.GetFormats(false);
                    if (formats.Length == 0) return new ClipboardBackup(null, true);

                    var copy = new DataObject();
                    foreach (var f in formats)
                    {
                        try
                        {
                            var d = src.GetData(f, false);
                            if (d is not null) copy.SetData(f, d, false);
                        }
                        catch
                        {
                            // 某些格式（延迟渲染、私有格式）无法读取，跳过
                        }
                    }
                    return new ClipboardBackup(copy, false);
                }
                catch (COMException)
                {
                    Thread.Sleep(20);
                }
            }
            return new ClipboardBackup(null, false); // 读取失败：不做还原，以免清空用户剪贴板
        }

        public void Restore()
        {
            if (_data is not null)
                Clipboard.SetDataObject(_data, true); // copy=true：程序退出后内容仍保留
            else if (_wasEmpty)
                Clipboard.Clear();
        }
    }
}
