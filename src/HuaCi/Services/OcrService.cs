using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HuaCi.Services;

public sealed record OcrLanguageInfo(string Tag, string DisplayName)
{
    public override string ToString() => $"{DisplayName}（{Tag}）";
}

public sealed record OcrOutcome(string Text, string LanguageTag);

/// <summary>
/// 使用 Windows 内置 OCR（Windows.Media.Ocr）识别图片中的文字，完全在本地运行。
/// DeepSeek 接口不支持图片输入，所以截图翻译先本地 OCR 再翻译文字。
/// </summary>
public static class OcrService
{
    public const string NoLanguageHelp =
        "系统没有安装任何 OCR 语言，无法识别截图中的文字。\n" +
        "添加方法：打开“设置 → 时间和语言 → 语言和区域”，点击“添加语言”，选择“中文(简体，中国)”或“English (United States)”，" +
        "安装时勾选“光学字符识别”（Windows 10 中通常随“基本键入”一起安装），完成后重启本程序。\n" +
        "也可以用管理员身份打开 PowerShell 运行：\n" +
        "Add-WindowsCapability -Online -Name \"Language.OCR~~~zh-CN~0.0.1.0\"";

    /// <summary>已安装的 OCR 语言。</summary>
    public static IReadOnlyList<OcrLanguageInfo> GetInstalledLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => new OcrLanguageInfo(l.LanguageTag, l.DisplayName))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("获取 OCR 语言失败：" + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// 识别文字。preferredTag 为空时自动选择：
    /// 依次尝试用户语言配置对应的识别器和其他已安装语言（最多 4 种），按结果质量打分选最好的。
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(Bitmap image, string? preferredTag, CancellationToken ct = default)
    {
        var languages = OcrEngine.AvailableRecognizerLanguages.ToList();
        if (languages.Count == 0) throw new TranslationException(NoLanguageHelp);

        using var prepared = Prepare(image);
        using var softwareBitmap = ToSoftwareBitmap(prepared);

        // 指定了语言且已安装：只用这一种
        if (!string.IsNullOrWhiteSpace(preferredTag))
        {
            var lang = languages.FirstOrDefault(l => string.Equals(l.LanguageTag, preferredTag, StringComparison.OrdinalIgnoreCase));
            if (lang is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null)
                {
                    var text = await RunAsync(engine, softwareBitmap, ct);
                    return new OcrOutcome(text, lang.LanguageTag);
                }
            }
            Log.Warn($"OCR 语言 {preferredTag} 未安装，改为自动选择");
        }

        // 自动：候选顺序 = 用户语言配置 → 中文 → 英文 → 其他
        var candidates = new List<Language>();
        void Add(Language? l)
        {
            if (l is not null && !candidates.Any(c => c.LanguageTag == l.LanguageTag)) candidates.Add(l);
        }
        var profile = OcrEngine.TryCreateFromUserProfileLanguages();
        Add(profile?.RecognizerLanguage);
        Add(languages.FirstOrDefault(l => l.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
                                          || l.LanguageTag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)));
        Add(languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)));
        foreach (var l in languages) Add(l);

        OcrOutcome? best = null;
        double bestScore = double.MinValue;
        foreach (var lang in candidates.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is null) continue;
            string text;
            try { text = await RunAsync(engine, softwareBitmap, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"OCR（{lang.LanguageTag}）失败：{ex.Message}");
                continue;
            }
            double score = Score(text);
            Log.Info($"OCR {lang.LanguageTag} 得分 {score:F1}，{text.Length} 字");
            if (score > bestScore)
            {
                bestScore = score;
                best = new OcrOutcome(text, lang.LanguageTag);
            }
            // 第一个候选结果已经很"干净"时不再尝试其他语言，节省时间
            if (best is not null && ReferenceEquals(lang, candidates[0]) && IsClean(text)) break;
        }

        return best ?? new OcrOutcome("", candidates.FirstOrDefault()?.LanguageTag ?? "");
    }

    private static async Task<string> RunAsync(OcrEngine engine, SoftwareBitmap bitmap, CancellationToken ct)
    {
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return JoinLines(result);
    }

    /// <summary>
    /// 把 OCR 行拼成段落：CJK 字符之间不加空格（Windows OCR 会把每个汉字当成一个"词"），
    /// 同一段内的换行合并，行距明显变大时视为新段落。
    /// </summary>
    private static string JoinLines(OcrResult result)
    {
        var lines = new List<(string Text, double Top, double Bottom)>();
        foreach (var line in result.Lines)
        {
            var sb = new StringBuilder();
            string? prev = null;
            double top = double.MaxValue, bottom = double.MinValue;
            foreach (var w in line.Words)
            {
                var t = w.Text;
                if (prev is not null && NeedsSpace(prev[^1], t[0])) sb.Append(' ');
                sb.Append(t);
                prev = t;
                top = Math.Min(top, w.BoundingRect.Top);
                bottom = Math.Max(bottom, w.BoundingRect.Bottom);
            }
            if (sb.Length > 0) lines.Add((sb.ToString(), top, bottom));
        }
        if (lines.Count == 0) return "";

        var heights = lines.Select(l => l.Bottom - l.Top).Where(h => h > 0).OrderBy(h => h).ToList();
        double median = heights.Count > 0 ? heights[heights.Count / 2] : 0;

        var output = new StringBuilder(lines[0].Text);
        for (int i = 1; i < lines.Count; i++)
        {
            var (text, top, _) = lines[i];
            double gap = top - lines[i - 1].Bottom;
            bool newParagraph = median > 0 && gap > median * 0.9;
            var last = output[^1];
            if (newParagraph)
            {
                output.Append('\n');
            }
            else if (last == '-' && char.IsLower(text[0]))
            {
                output.Length--; // 连字符断词：exam- ple → example
            }
            else if (NeedsSpace(last, text[0]))
            {
                output.Append(' ');
            }
            output.Append(text);
        }
        return output.ToString().Trim();
    }

    private static bool IsCjk(char c) =>
        c is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF'
            or >= '\u3000' and <= '\u303F';

    private static bool NeedsSpace(char prev, char next) => !(IsCjk(prev) || IsCjk(next));

    /// <summary>粗略评估识别质量：字母 / 汉字 / 数字越多越好，奇怪符号越多越差。</summary>
    private static double Score(string text)
    {
        double good = 0, bad = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r)) good += r.Value > 0x2E80 ? 1.5 : 1; // 一个汉字信息量更大
            else if (Rune.IsWhiteSpace(r) || ".,;:!?'\"()-，。；：！？、“”‘’（）《》".Contains(r.ToString())) { }
            else bad++;
        }
        return good - bad * 2;
    }

    private static bool IsClean(string text)
    {
        if (text.Length < 4) return false;
        int weird = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsPunctuation(c));
        return weird <= text.Length * 0.05;
    }

    /// <summary>缩放到适合 OCR 的尺寸：小图放大（小字号识别更准），超大图缩小到引擎上限。</summary>
    private static Bitmap Prepare(Bitmap src)
    {
        double max = OcrEngine.MaxImageDimension;
        double scale = 1;
        int shortSide = Math.Min(src.Width, src.Height);
        if (shortSide < 120) scale = Math.Min(3, 120.0 / Math.Max(1, shortSide));
        else if (shortSide < 300) scale = 2;
        scale = Math.Min(scale, max / Math.Max(src.Width, src.Height));
        if (Math.Abs(scale - 1) < 0.01) return (Bitmap)src.Clone();

        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            // 截图没有透明度：把 alpha 统一设为 255
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
