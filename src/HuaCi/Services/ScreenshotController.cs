using System.Windows;
using HuaCi.Native;

namespace HuaCi.Services;

/// <summary>截图翻译流程：截屏 → 框选 → 本地 OCR → DeepSeek 翻译 → 选区旁显示结果。</summary>
public sealed class ScreenshotController
{
    private readonly SettingsService _settings;
    private readonly TranslationController _translator;
    private readonly Action _beforeCapture;
    private bool _busy;

    public ScreenshotController(SettingsService settings, TranslationController translator, Action beforeCapture)
    {
        _settings = settings;
        _translator = translator;
        _beforeCapture = beforeCapture;
    }

    public async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        ScreenSnapshot? snapshot = null;
        NativeMethods.RECT? selected = null;
        try
        {
            // 关闭本程序的弹窗 / 划词按钮，等它们从屏幕上消失后再截图
            _beforeCapture();
            await Task.Delay(120);

            snapshot = ScreenSnapshot.CaptureAll();
            var region = await new ScreenshotSelector().SelectAsync(snapshot);
            if (region is not { } r) return;
            selected = r;

            using var image = snapshot.Crop(r);
            snapshot.Dispose();
            snapshot = null;

            OcrOutcome outcome;
            try
            {
                outcome = await OcrService.RecognizeAsync(image, _settings.Current.OcrLanguage);
            }
            catch (TranslationException ex)
            {
                _translator.ShowOcrProblem(r, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("OCR 失败", ex);
                _translator.ShowOcrProblem(r, "文字识别失败：" + ex.Message +
                    "\n截图翻译需要 Windows 10 1903 或更高版本。");
                return;
            }

            if (string.IsNullOrWhiteSpace(outcome.Text))
            {
                _translator.ShowOcrProblem(r, $"没有在选区中识别到文字（OCR 语言：{outcome.LanguageTag}）。\n" +
                    "可以把选区框得更贴近文字，或在“设置 → 截图翻译”中指定 OCR 语言。");
                return;
            }

            _translator.TranslateOcr(outcome.Text, r);
        }
        catch (Exception ex)
        {
            Log.Error("截图翻译失败", ex);
            var msg = "截图翻译失败：" + ex.Message + "\n详细信息已写入日志：%AppData%\\HuaCi\\log.txt";
            try
            {
                if (selected is { } r) _translator.ShowOcrProblem(r, msg);
                else _translator.ShowProblemAtCursor(msg);
            }
            catch (Exception inner)
            {
                // 弹窗本身也失败时才退回系统对话框
                Log.Error("显示错误弹窗失败", inner);
                MessageBox.Show(msg, "划词翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            snapshot?.Dispose();
            _busy = false;
        }
    }
}
