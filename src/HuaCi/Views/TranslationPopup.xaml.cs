using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Services;

namespace HuaCi.Views;

/// <summary>鼠标旁的翻译结果弹窗。</summary>
public partial class TranslationPopup : Window
{
    private const int CursorOffset = 12; // 物理像素，会乘以缩放
    private readonly MouseHook _outsideClickHook = new();
    private NativeMethods.POINT _anchor;
    private bool _growUp;      // 放在鼠标上方时，向上生长
    private int _aboveBottom;  // 向上生长时窗口底边的位置
    private bool _userMoved;   // 用户拖动过则不再跟随锚点
    private IntPtr _hwnd;
    private bool _closing;

    public event EventHandler? RetryRequested;

    /// <summary>用户编辑原文后点击"重新翻译"（参数为新原文）。</summary>
    public event EventHandler<string>? RetranslateRequested;
    public event EventHandler? OpenSettingsRequested;

    public bool IsPinned => PinButton.IsChecked == true;

    public bool IsClosed { get; private set; }

    public string ResultText => ResultBox.Text;

    public TranslationPopup()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.AddExStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW); // 不出现在 Alt+Tab
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (!IsPinned) SafeClose(); };
        SizeChanged += (_, _) => KeepOnScreen();
        Closed += (_, _) => { IsClosed = true; _outsideClickHook.Dispose(); };
        _outsideClickHook.MouseEvent += OnGlobalMouse;
    }

    // ---------------- 内容 ----------------

    public void Reset(string source, TranslationRequest req, string model, bool expandSource, bool fromOcr = false)
    {
        CustomRetry = null;
        FallbackAction = null;
        SourceKindText.Text = fromOcr ? "识别结果（可修改后重新翻译）" : "原文（可修改后重新翻译）";
        var lang = req.SourceIsChinese ? "中文" : "自动检测";
        if (req.AlreadyInTarget) lang = "中文";
        DirectionText.Text = $"{lang} → {req.TargetLanguage}" + (req.IsSingleWord ? "  ·  词典" : "");
        SourceExpander.Visibility = Visibility.Visible;
        SourceBox.Text = source;
        var firstLine = source.Trim().Split('\n')[0].Trim();
        SourceHeader.Text = (fromOcr ? "识别：" : "原文：") + (firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine);
        SourceExpander.IsExpanded = expandSource;
        SetModel(model);

        ResultBox.Text = "";
        ResultBox.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = "翻译中…";
        _thinkingChars = 0;
        CopyButton.IsEnabled = false;
    }

    public void SetFooter(string text) => FooterText.Text = text;

    public void SetModel(string model) =>
        FooterText.Text = string.IsNullOrWhiteSpace(model) ? "DeepSeek" : $"DeepSeek · {model}";

    private int _thinkingChars;

    /// <summary>收到 reasoning_content（思考过程）时显示进度，不计入译文。</summary>
    public void ShowThinking(int addedChars)
    {
        if (ResultBox.Visibility == Visibility.Visible) return;
        _thinkingChars += addedChars;
        LoadingText.Text = $"思考中…（{_thinkingChars} 字）";
    }

    /// <summary>在结果区显示一条普通提示（灰色）。</summary>
    public void ShowNotice(string message)
    {
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = message;
        LoadingText.TextWrapping = TextWrapping.Wrap;
        ResultBox.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    public void AppendResult(string piece)
    {
        if (ResultBox.Visibility != Visibility.Visible)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            ResultBox.Visibility = Visibility.Visible;
            // 模型有时以换行开头
            piece = piece.TrimStart('\r', '\n');
        }
        ResultBox.AppendText(piece);
        ResultBox.ScrollToEnd();
        CopyButton.IsEnabled = true;
    }

    public void MarkDone()
    {
        ResultBox.Text = ResultBox.Text.TrimEnd();
        ResultBox.ScrollToHome();
    }

    public void ShowError(string message, bool needsSettings)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        SettingsButton.Visibility = needsSettings ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = needsSettings ? Visibility.Collapsed : Visibility.Visible;
        FallbackButton.Visibility = FallbackAction is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>重试时执行的自定义操作（例如识图翻译重新发送图片）；为 null 时按原文重新翻译。</summary>
    public Action? CustomRetry { get; set; }

    /// <summary>识图失败时可用的"改用 Windows OCR"操作。</summary>
    public Action? FallbackAction { get; set; }

    /// <summary>准备显示识图翻译结果。</summary>
    public void ResetForVision(string targetDescription, string model, bool showSource)
    {
        DirectionText.Text = $"截图 → {targetDescription}";
        SourceExpander.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        SourceExpander.IsExpanded = false;
        SourceBox.Text = "";
        SourceHeader.Text = "识别出的原文：等待中…";
        SourceKindText.Text = "识别结果（可修改后重新翻译）";
        FooterText.Text = $"DeepSeek 识图 · {model}";
        ResultBox.Text = "";
        ResultBox.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = "正在识别图片并翻译…";
        _thinkingChars = 0;
        CopyButton.IsEnabled = false;
    }

    /// <summary>流式更新识别出的原文。</summary>
    public void SetRecognizedSource(string text)
    {
        var t = text.Trim();
        SourceBox.Text = t;
        var firstLine = t.Split('\n')[0].Trim();
        SourceHeader.Text = "识别：" + (firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine);
    }

    /// <summary>仅显示一条提示（例如没有选中文字）。</summary>
    public void ShowMessage(string message, string title = "划词翻译", bool selectable = false)
    {
        DirectionText.Text = title;
        SourceExpander.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        if (selectable)
        {
            // 用只读文本框显示，方便复制其中的命令
            LoadingText.Visibility = Visibility.Collapsed;
            ResultBox.Text = message;
            ResultBox.Visibility = Visibility.Visible;
            CopyButton.IsEnabled = true;
        }
        else
        {
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = message;
            ResultBox.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = false;
        }
    }

    // ---------------- 显示与定位 ----------------

    /// <summary>在鼠标位置附近显示（物理像素坐标）。</summary>
    public void ShowNear(NativeMethods.POINT anchor)
    {
        int off = (int)(CursorOffset * ScreenHelper.GetScale(anchor));
        var excl = new NativeMethods.RECT { Left = anchor.X - off, Top = anchor.Y - off, Right = anchor.X + off, Bottom = anchor.Y + off };
        ShowAnchored(excl, alignLeft: false);
    }

    /// <summary>显示在某个屏幕区域（例如截图选区）旁边：优先放在下方并左对齐，放不下则放上方。</summary>
    public void ShowBeside(NativeMethods.RECT region)
    {
        int gap = (int)(6 * ScreenHelper.GetScale(new NativeMethods.POINT(region.Left, region.Bottom)));
        var excl = new NativeMethods.RECT { Left = region.Left, Top = region.Top - gap, Right = region.Right, Bottom = region.Bottom + gap };
        ShowAnchored(excl, alignLeft: true);
    }

    /// <summary>
    /// 在"排除区"旁边显示：下方放得下就放下方（向下生长），否则放上方（向上生长）。
    /// alignLeft=true 时与排除区左边对齐，否则放在其右侧。
    /// </summary>
    private void ShowAnchored(NativeMethods.RECT excl, bool alignLeft)
    {
        _userMoved = false;
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();

        // 参考点：排除区下边缘（截图选区很大时取靠近底部的位置所在的显示器）
        var refPt = new NativeMethods.POINT(alignLeft ? excl.Left : excl.Right, excl.Bottom);
        var wa = ScreenHelper.GetWorkArea(refPt);
        if (refPt.Y >= wa.Bottom) refPt = new NativeMethods.POINT(refPt.X, Math.Max(excl.Top, wa.Top));
        _anchor = refPt;

        // 先把窗口移到目标显示器，使 WPF 按该显示器 DPI 缩放（PerMonitorV2）
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, refPt.X, refPt.Y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        if (!IsVisible) Show();
        UpdateLayout();

        double scale = ScreenHelper.GetScale(refPt);
        wa = ScreenHelper.GetWorkArea(refPt);
        NativeMethods.GetWindowRect(_hwnd, out var r);
        // 预估最终高度，避免流式输出时窗口超出屏幕底部
        int expected = Math.Max(r.Height, (int)(260 * scale));
        bool roomBelow = excl.Bottom + expected <= wa.Bottom;
        bool roomAbove = excl.Top - expected >= wa.Top;
        _growUp = !roomBelow && (roomAbove || excl.Top - wa.Top > wa.Bottom - excl.Bottom);
        _aboveBottom = excl.Top;

        int x = alignLeft ? excl.Left : excl.Right;
        if (x + r.Width > wa.Right) x = alignLeft ? wa.Right - r.Width : excl.Left - r.Width;
        int y = _growUp ? excl.Top - r.Height : excl.Bottom;
        (x, y) = ScreenHelper.Clamp(wa, x, y, r.Width, r.Height);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _outsideClickHook.Install();
        ActivateSafely();
    }

    private void ActivateSafely()
    {
        if (!Activate()) NativeMethods.SetForegroundWindow(_hwnd);
        // 让 Esc 能直接生效
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(ResultBox.IsVisible ? ResultBox : this));
    }

    /// <summary>内容变化后保持窗口在屏幕内。</summary>
    private void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var center = new NativeMethods.POINT(r.Left + r.Width / 2, r.Top + Math.Min(r.Height / 2, 40));
        var wa = ScreenHelper.GetWorkArea(_userMoved ? center : _anchor);

        int x = r.Left, y = r.Top;
        if (_growUp && !_userMoved) y = _aboveBottom - r.Height;
        (x, y) = ScreenHelper.Clamp(wa, x, y, r.Width, r.Height);
        if (x != r.Left || y != r.Top)
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // ---------------- 关闭逻辑 ----------------

    private void OnGlobalMouse(object? sender, MouseHookEventArgs e)
    {
        if (IsPinned || _closing) return;
        if (e.Message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN))
            return;
        if (NativeMethods.GetWindowRect(_hwnd, out var r) && !r.Contains(e.Point))
            Dispatcher.BeginInvoke(SafeClose);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SafeClose();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && ResultBox.SelectionLength == 0
                 && !SourceBox.IsKeyboardFocusWithin)
        {
            // 未选中任何文字时，Ctrl+C 复制全部译文
            e.Handled = true;
            CopyResult();
        }
    }

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        _outsideClickHook.Uninstall();
        try { Close(); } catch (InvalidOperationException) { }
    }

    // ---------------- 按钮 ----------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            _userMoved = true;
        }
        catch (InvalidOperationException) { }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void CopyResult()
    {
        var text = ResultBox.Text.Trim();
        if (text.Length == 0) return;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                CopyButton.Content = "\uE73E"; // 对勾
                CopyButton.ToolTip = "已复制";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                t.Tick += (_, _) => { t.Stop(); CopyButton.Content = "\uE8C8"; CopyButton.ToolTip = "复制译文"; };
                t.Start();
                return;
            }
            catch (COMException) { Thread.Sleep(30); }
        }
    }

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        PinButton.Content = IsPinned ? "\uE840" : "\uE718";
        PinButton.ToolTip = IsPinned ? "取消固定" : "固定窗口（点击外部不关闭）";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SafeClose();

    private void RetranslateButton_Click(object sender, RoutedEventArgs e) => RequestRetranslate();

    private void SourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter：用修改后的原文重新翻译
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            RequestRetranslate();
        }
    }

    private void RequestRetranslate()
    {
        var text = SourceBox.Text.Trim();
        if (text.Length > 0) RetranslateRequested?.Invoke(this, text);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRetry is { } retry) retry();
        else RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FallbackButton_Click(object sender, RoutedEventArgs e) => FallbackAction?.Invoke();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        if (!IsPinned) SafeClose();
    }
}
