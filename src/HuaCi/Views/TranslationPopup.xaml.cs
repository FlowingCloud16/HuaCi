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
    private bool _userMoved;   // 用户拖动过则不再跟随锚点
    private IntPtr _hwnd;
    private bool _closing;

    public event EventHandler? RetryRequested;
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

    public void Reset(string source, TranslationRequest req, string model, bool expandSource)
    {
        var lang = req.SourceIsChinese ? "中文" : "自动检测";
        DirectionText.Text = $"{lang} → {req.TargetLanguage}" + (req.IsSingleWord ? "  ·  词典" : "");
        SourceExpander.Visibility = Visibility.Visible;
        SourceBox.Text = source;
        var firstLine = source.Trim().Split('\n')[0].Trim();
        SourceHeader.Text = "原文：" + (firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine);
        SourceExpander.IsExpanded = expandSource;
        FooterText.Text = $"DeepSeek · {model}";

        ResultBox.Text = "";
        ResultBox.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = "翻译中…";
        CopyButton.IsEnabled = false;
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
    }

    /// <summary>仅显示一条提示（例如没有选中文字）。</summary>
    public void ShowMessage(string message)
    {
        DirectionText.Text = "划词翻译";
        SourceExpander.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = message;
        ResultBox.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
    }

    // ---------------- 显示与定位 ----------------

    /// <summary>在鼠标位置附近显示（物理像素坐标）。</summary>
    public void ShowNear(NativeMethods.POINT anchor)
    {
        _anchor = anchor;
        _userMoved = false;

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();

        // 先把窗口移到目标显示器，使 WPF 按该显示器 DPI 缩放（PerMonitorV2）
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, anchor.X, anchor.Y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        if (!IsVisible) Show();
        UpdateLayout();

        double scale = ScreenHelper.GetScale(anchor);
        var wa = ScreenHelper.GetWorkArea(anchor);
        NativeMethods.GetWindowRect(_hwnd, out var r);
        int offset = (int)(CursorOffset * scale);
        // 预估最终高度，避免流式输出时窗口超出屏幕底部
        int expected = Math.Max(r.Height, (int)(260 * scale));
        _growUp = anchor.Y + offset + expected > wa.Bottom && anchor.Y - wa.Top > wa.Bottom - anchor.Y;

        int x = anchor.X + offset;
        if (x + r.Width > wa.Right) x = anchor.X - offset - r.Width;
        int y = _growUp ? anchor.Y - offset - r.Height : anchor.Y + offset;
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
        if (_growUp && !_userMoved)
        {
            int offset = (int)(CursorOffset * ScreenHelper.GetScale(_anchor));
            y = _anchor.Y - offset - r.Height;
        }
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

    private void RetryButton_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        if (!IsPinned) SafeClose();
    }
}
