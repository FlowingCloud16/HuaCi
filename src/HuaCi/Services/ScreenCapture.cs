using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuaCi.Native;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace HuaCi.Services;

/// <summary>一次截图：每个显示器一张冻结的位图（物理像素）。</summary>
public sealed class ScreenSnapshot : IDisposable
{
    public sealed record MonitorShot(ScreenHelper.MonitorInfo Monitor, Bitmap Bitmap);

    public IReadOnlyList<MonitorShot> Shots { get; }

    private ScreenSnapshot(List<MonitorShot> shots) => Shots = shots;

    /// <summary>截取所有显示器。进程为 Per-Monitor V2 DPI 感知，坐标均为物理像素。</summary>
    public static ScreenSnapshot CaptureAll()
    {
        var shots = new List<MonitorShot>();
        try
        {
            foreach (var m in ScreenHelper.GetMonitors())
            {
                var r = m.Bounds;
                if (r.Width <= 0 || r.Height <= 0) continue;
                shots.Add(new MonitorShot(m, CaptureRect(r)));
            }
        }
        catch
        {
            foreach (var s in shots) s.Bitmap.Dispose();
            throw;
        }
        if (shots.Count == 0) throw new InvalidOperationException("没有找到可截取的显示器。");
        return new ScreenSnapshot(shots);
    }

    /// <summary>
    /// 用 GDI BitBlt 截取屏幕矩形（物理像素）。使用 SRCCOPY | CAPTUREBLT 以包含分层窗口；
    /// System.Drawing 的 CopyFromScreen 会校验枚举并拒绝 CAPTUREBLT，所以这里直接 P/Invoke。
    /// </summary>
    private static Bitmap CaptureRect(NativeMethods.RECT r)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero); // 整个虚拟屏幕
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法获取屏幕设备上下文。");
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, r.Width, r.Height);
            if (memDc == IntPtr.Zero || hBitmap == IntPtr.Zero)
                throw new InvalidOperationException($"创建截图缓冲区失败（{r.Width}×{r.Height}）。");
            old = NativeMethods.SelectObject(memDc, hBitmap);
            if (!NativeMethods.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.Left, r.Top,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new InvalidOperationException($"截屏失败（BitBlt 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
            NativeMethods.SelectObject(memDc, old);
            old = IntPtr.Zero;

            // FromHbitmap 得到的是 32bppRgb 副本，再统一转成 32bppArgb，后续处理只需考虑一种格式
            using var tmp = Image.FromHbitmap(hBitmap);
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(tmp, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(0, 0, r.Width, r.Height), GraphicsUnit.Pixel);
            }
            return bmp;
        }
        finally
        {
            if (old != IntPtr.Zero) NativeMethods.SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>从冻结的截图中裁剪任意区域（可跨显示器）。</summary>
    public Bitmap Crop(NativeMethods.RECT region)
    {
        var result = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(System.Drawing.Color.White);
        foreach (var s in Shots)
        {
            var b = s.Monitor.Bounds;
            int l = Math.Max(region.Left, b.Left), t = Math.Max(region.Top, b.Top);
            int rr = Math.Min(region.Right, b.Right), bb = Math.Min(region.Bottom, b.Bottom);
            if (rr <= l || bb <= t) continue;
            var src = new Rectangle(l - b.Left, t - b.Top, rr - l, bb - t);
            var dst = new Rectangle(l - region.Left, t - region.Top, rr - l, bb - t);
            g.DrawImage(s.Bitmap, dst, src, GraphicsUnit.Pixel);
        }
        return result;
    }

    /// <summary>转换为 WPF 可显示的 BitmapSource（冻结，可跨线程）。</summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        foreach (var s in Shots) s.Bitmap.Dispose();
    }
}
