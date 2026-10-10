using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Views;

namespace HuaCi.Services;

/// <summary>在所有显示器上显示遮罩，让用户框选一个区域。返回物理像素矩形，取消时返回 null。</summary>
public sealed class ScreenshotSelector
{
    private const int MinSize = 6; // 物理像素，太小的选区视为误触

    private readonly List<ScreenshotOverlay> _overlays = new();
    private readonly TaskCompletionSource<NativeMethods.RECT?> _tcs = new();
    private NativeMethods.POINT _start;
    private bool _finished;

    public bool IsDragging { get; private set; }

    public NativeMethods.RECT? Selection { get; private set; }

    public Task<NativeMethods.RECT?> SelectAsync(ScreenSnapshot snapshot)
    {
        var cursor = ScreenHelper.GetCursorPos();
        ScreenshotOverlay? active = null;
        foreach (var shot in snapshot.Shots)
        {
            bool here = shot.Monitor.Bounds.Contains(cursor);
            var overlay = new ScreenshotOverlay(shot.Monitor, ScreenSnapshot.ToBitmapSource(shot.Bitmap), this, showHint: here);
            _overlays.Add(overlay);
            overlay.ShowOnMonitor();
            if (here) active = overlay;
        }
        active ??= _overlays.FirstOrDefault();
        if (active is not null)
        {
            active.Activate();
            if (!active.IsActive)
                NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(active).Handle);
            active.Focus();
        }
        return _tcs.Task;
    }

    public void Begin(NativeMethods.POINT p)
    {
        if (_finished) return;
        IsDragging = true;
        _start = p;
        Update(p);
    }

    public void Update(NativeMethods.POINT p)
    {
        if (!IsDragging || _finished) return;
        Selection = new NativeMethods.RECT
        {
            Left = Math.Min(_start.X, p.X),
            Top = Math.Min(_start.Y, p.Y),
            Right = Math.Max(_start.X, p.X) + 1,
            Bottom = Math.Max(_start.Y, p.Y) + 1,
        };
        foreach (var o in _overlays) o.Render(Selection);
    }

    public void End(NativeMethods.POINT p)
    {
        if (!IsDragging || _finished) return;
        Update(p);
        IsDragging = false;
        if (Selection is { } s && s.Width >= MinSize && s.Height >= MinSize)
        {
            Finish(s);
        }
        else
        {
            // 选区太小（单击）：清除，允许重新框选
            Selection = null;
            foreach (var o in _overlays) o.Render(null);
        }
    }

    public void Cancel() => Finish(null);

    /// <summary>用户切换到其他程序时取消截图。</summary>
    public void OnOverlayDeactivated()
    {
        if (_finished) return;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_finished && !IsDragging && !ScreenHelper.IsForegroundOurs()) Cancel();
        });
    }

    private void Finish(NativeMethods.RECT? result)
    {
        if (_finished) return;
        _finished = true;
        IsDragging = false;
        foreach (var o in _overlays)
        {
            try { o.Close(); } catch (InvalidOperationException) { }
        }
        _overlays.Clear();
        _tcs.TrySetResult(result);
    }
}
