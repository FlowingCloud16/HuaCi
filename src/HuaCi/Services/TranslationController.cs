using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Views;

namespace HuaCi.Services;

/// <summary>协调"取词 → 弹窗 → 流式翻译"的整个流程。所有方法在 UI 线程调用。</summary>
public sealed class TranslationController : IDisposable
{
    private const int MaxChars = 6000;

    private readonly SettingsService _settings;
    private readonly DeepSeekClient _client = new();
    private readonly Action _openSettings;
    private TranslationPopup? _popup;
    private CancellationTokenSource? _cts;
    private bool _capturing;

    public TranslationController(SettingsService settings, Action openSettings)
    {
        _settings = settings;
        _openSettings = openSettings;
    }

    /// <summary>获取当前选中文字并翻译。</summary>
    /// <param name="silentIfEmpty">划词按钮触发时为 true：取不到文字就静默结束，不弹出提示。</param>
    public async Task TranslateSelectionAsync(bool silentIfEmpty = false)
    {
        if (_capturing) return;
        _capturing = true;
        var anchor = ScreenHelper.GetCursorPos();
        string? text;
        try
        {
            text = await SelectionCapture.CaptureAsync();
        }
        catch (Exception ex)
        {
            Log.Error("获取选中文字失败", ex);
            text = null;
        }
        finally
        {
            _capturing = false;
        }

        if (text is null)
        {
            if (silentIfEmpty) return;
            ShowTransientMessage(anchor, "没有获取到选中的文字。\n请先选中文字再按快捷键；以管理员身份运行的程序需要本工具也以管理员身份运行。");
            return;
        }

        Translate(text, anchor);
    }

    public void Translate(string text, NativeMethods.POINT anchor, TranslationPopup? reuse = null) =>
        TranslateCore(text, p => p.ShowNear(anchor), reuse, fromOcr: false);

    /// <summary>截图翻译：弹窗显示在选区旁边，原文默认展开以便修正识别错误。</summary>
    public void TranslateOcr(string text, NativeMethods.RECT region) =>
        TranslateCore(text, p => p.ShowBeside(region), null, fromOcr: true);

    /// <summary>截图翻译失败（无 OCR 语言、未识别到文字等）时在选区旁显示提示。</summary>
    public void ShowOcrProblem(NativeMethods.RECT region, string message)
    {
        _cts?.Cancel();
        var p = GetPopup(out bool reusePinned);
        p.ShowMessage(message, "截图翻译", selectable: true);
        if (!reusePinned) p.ShowBeside(region);
    }

    /// <summary>在鼠标旁用弹窗显示一条错误（替代系统 MessageBox）。</summary>
    public void ShowProblemAtCursor(string message, string title = "截图翻译")
    {
        _cts?.Cancel();
        var p = GetPopup(out bool reusePinned);
        p.ShowMessage(message, title, selectable: true);
        if (!reusePinned) p.ShowNear(ScreenHelper.GetCursorPos());
    }

    private void TranslateCore(string text, Action<TranslationPopup> place, TranslationPopup? reuse, bool fromOcr)
    {
        text = text.Trim();
        bool truncated = false;
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
            truncated = true;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var settings = _settings.Current;
        var req = PromptBuilder.Build(text, settings);
        bool reuseInPlace;
        TranslationPopup popup;
        if (reuse is { IsClosed: false } && ReferenceEquals(reuse, _popup))
        {
            popup = reuse;
            reuseInPlace = true;
        }
        else
        {
            popup = GetPopup(out reuseInPlace);
        }
        bool expand = reuseInPlace && popup.SourceExpander.Visibility == System.Windows.Visibility.Visible
            ? popup.SourceExpander.IsExpanded
            : settings.ShowSourceExpanded || fromOcr;
        popup.Reset(text, req, settings.Model, expand, fromOcr);
        if (!reuseInPlace) place(popup);

        _ = RunAsync(popup, req, settings, truncated, ct);
    }

    private async Task RunAsync(TranslationPopup popup, TranslationRequest req, AppSettings settings, bool truncated, CancellationToken ct)
    {
        try
        {
            if (req.AlreadyInTarget)
            {
                popup.ShowNotice("原文已是中文，无需翻译。\n如需把中文译成英文等其他语言，请在“设置 → 翻译”中开启“中文原文改译为”。");
                return;
            }

            settings = await EnsureModelAsync(settings, ct);
            if (ct.IsCancellationRequested || popup.IsClosed) return;
            popup.SetModel(settings.Model);

            await foreach (var chunk in _client.TranslateStreamAsync(req, settings, ct))
            {
                if (ct.IsCancellationRequested || popup.IsClosed) return;
                if (chunk.IsReasoning) popup.ShowThinking(chunk.Text.Length);
                else popup.AppendResult(chunk.Text);
            }
            if (ct.IsCancellationRequested) return;
            if (truncated) popup.AppendResult($"\n\n（原文过长，仅翻译了前 {MaxChars} 个字符）");
            popup.MarkDone();
        }
        catch (OperationCanceledException)
        {
            // 新的翻译开始或弹窗关闭
        }
        catch (TranslationException ex)
        {
            if (!ct.IsCancellationRequested && !popup.IsClosed) popup.ShowError(ex.Message, ex.NeedsSettings);
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            if (!ct.IsCancellationRequested && !popup.IsClosed) popup.ShowError("翻译失败：" + ex.Message, false);
        }
    }

    /// <summary>未选择模型时，自动使用 GET /models 返回的第一个模型并保存。</summary>
    private async Task<AppSettings> EnsureModelAsync(AppSettings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.Model)) return settings;
        var key = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new TranslationException("尚未设置 API Key，请在“设置”中填写。", needsSettings: true);

        var models = await _client.ListModelsAsync(settings.BaseUrl, key, ct);
        if (models.Count == 0)
            throw new TranslationException("接口没有返回可用模型，请在“设置”中手动填写模型名称。", needsSettings: true);

        var updated = _settings.Current.Clone();
        updated.Model = models[0].Id;
        try { _settings.Save(updated); }
        catch (Exception ex) { Log.Warn("保存自动选择的模型失败：" + ex.Message); }
        Log.Info("未选择模型，自动使用 " + updated.Model);

        var copy = settings.Clone();
        copy.Model = updated.Model;
        return copy;
    }

    private TranslationPopup GetPopup(out bool reusePinned)
    {
        if (_popup is { IsClosed: false, IsPinned: true })
        {
            reusePinned = true;
            return _popup;
        }

        reusePinned = false;
        // 先置空再关闭旧弹窗，避免旧弹窗的 Closed 回调取消新的翻译任务
        var old = _popup;
        _popup = null;
        old?.SafeClose();

        var p = new TranslationPopup();
        p.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, p))
            {
                _cts?.Cancel();
                _popup = null;
            }
        };
        p.RetryRequested += (_, _) =>
        {
            var src = p.SourceBox.Text;
            if (!string.IsNullOrWhiteSpace(src)) Translate(src, ScreenHelper.GetCursorPos(), p);
        };
        p.OpenSettingsRequested += (_, _) => _openSettings();
        p.RetranslateRequested += (_, text) => TranslateCore(text, _ => { }, p, fromOcr: false);
        _popup = p;
        return p;
    }

    private void ShowTransientMessage(NativeMethods.POINT anchor, string message)
    {
        _cts?.Cancel();
        if (_popup is { IsClosed: false, IsPinned: true })
        {
            _popup.ShowMessage(message);
            return;
        }
        var p = GetPopup(out _);
        p.ShowMessage(message);
        p.ShowNear(anchor);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!p.IsPinned) p.SafeClose();
        };
        timer.Start();
    }

    public void CloseActivePopup() => _popup?.SafeClose();

    public void Dispose()
    {
        _cts?.Cancel();
        _popup?.SafeClose();
        _client.Dispose();
    }
}
