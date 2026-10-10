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
        foreach (var m in ScreenHelper.GetMonitors())
        {
            var r = m.Bounds;
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                // CaptureBlt：同时截取分层窗口（例如其他程序的半透明窗口）
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(r.Width, r.Height),
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            }
            shots.Add(new MonitorShot(m, bmp));
        }
        return new ScreenSnapshot(shots);
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
