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

    /// <summary>获取当前选中文字并翻译（热键或小图标触发）。</summary>
    public async Task TranslateSelectionAsync()
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
            ShowTransientMessage(anchor, "没有获取到选中的文字。\n请先选中文字再按快捷键；以管理员身份运行的程序需要本工具也以管理员身份运行。");
            return;
        }

        Translate(text, anchor);
    }

    public void Translate(string text, NativeMethods.POINT anchor, TranslationPopup? reuse = null)
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
        popup.Reset(text, req, settings.Model, settings.ShowSourceExpanded);
        if (!reuseInPlace) popup.ShowNear(anchor);

        _ = RunAsync(popup, req, settings, truncated, ct);
    }

    private async Task RunAsync(TranslationPopup popup, TranslationRequest req, AppSettings settings, bool truncated, CancellationToken ct)
    {
        try
        {
            await foreach (var piece in _client.TranslateStreamAsync(req, settings, ct))
            {
                if (ct.IsCancellationRequested || popup.IsClosed) return;
                popup.AppendResult(piece);
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
