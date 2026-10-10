using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using HuaCi.Native;
using HuaCi.Services;

namespace HuaCi.Views;

/// <summary>设置窗口与宿主（App）之间的接口。</summary>
public interface ISettingsHost
{
    /// <summary>录制快捷键时临时注销全局热键，避免被拦截。</summary>
    void SuspendHotkey(bool suspend);

    /// <summary>应用新设置（注册热键、自启等）。失败时返回错误信息，设置不会被保存。</summary>
    bool TryApply(AppSettings settings, out string? error);
}

public partial class SettingsWindow : Window
{
    private static readonly string[] ZhTargets = ["English", "日本語", "한국어", "Français", "Deutsch", "Español", "Русский", "繁體中文"];
    private static readonly string[] OtherTargets = ["简体中文", "繁體中文", "English"];

    private readonly SettingsService _service;
    private readonly ISettingsHost _host;
    private HotkeyGesture _hotkey;
    private CancellationTokenSource? _testCts;

    public SettingsWindow(SettingsService service, ISettingsHost host)
    {
        InitializeComponent();
        _service = service;
        _host = host;
        foreach (var t in ZhTargets) TargetZhBox.Items.Add(t);
        foreach (var t in OtherTargets) TargetOtherBox.Items.Add(t);
        LoadFrom(service.Current);
        Closed += (_, _) => { _testCts?.Cancel(); _host.SuspendHotkey(false); };
    }

    private void LoadFrom(AppSettings s)
    {
        ApiKeyBox.Password = s.ApiKey;
        BaseUrlBox.Text = s.BaseUrl;
        ModelBox.Text = s.Model;
        TargetZhBox.Text = s.TargetLanguageForChinese;
        TargetOtherBox.Text = s.TargetLanguage;
        _hotkey = HotkeyGesture.TryParse(s.Hotkey, out var g) ? g : HotkeyGesture.Default;
        HotkeyBox.Text = _hotkey.ToString();
        bool autoStart;
        try { autoStart = AutoStartService.IsEnabled(); } catch { autoStart = s.AutoStart; }
        AutoStartCheck.IsChecked = autoStart;
        SelectIconCheck.IsChecked = s.EnableSelectIcon;
        ExpandSourceCheck.IsChecked = s.ShowSourceExpanded;
    }

    private string CurrentApiKey => (ShowKeyCheck.IsChecked == true ? ApiKeyPlainBox.Text : ApiKeyBox.Password).Trim();

    private AppSettings BuildSettings()
    {
        var s = _service.Current.Clone();
        s.ApiKey = CurrentApiKey;
        s.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? AppSettings.DefaultBaseUrl : BaseUrlBox.Text.Trim();
        s.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? AppSettings.DefaultModel : ModelBox.Text.Trim();
        s.TargetLanguageForChinese = string.IsNullOrWhiteSpace(TargetZhBox.Text) ? "English" : TargetZhBox.Text.Trim();
        s.TargetLanguage = string.IsNullOrWhiteSpace(TargetOtherBox.Text) ? "简体中文" : TargetOtherBox.Text.Trim();
        s.Hotkey = _hotkey.ToString();
        s.AutoStart = AutoStartCheck.IsChecked == true;
        s.EnableSelectIcon = SelectIconCheck.IsChecked == true;
        s.ShowSourceExpanded = ExpandSourceCheck.IsChecked == true;
        return s;
    }

    // ---------------- API Key 显示切换 ----------------

    private void ShowKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlainBox.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetBaseUrl_Click(object sender, RoutedEventArgs e) => BaseUrlBox.Text = AppSettings.DefaultBaseUrl;

    // ---------------- 测试连接 ----------------

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var s = BuildSettings();
        TestButton.IsEnabled = false;
        TestResult.Visibility = Visibility.Visible;
        TestResult.Foreground = (System.Windows.Media.Brush)FindResource("SubtleBrush");
        TestResult.Text = "正在测试…";
        try
        {
            using var client = new DeepSeekClient();
            var req = PromptBuilder.Build("Hello, world!", s);
            var sb = new System.Text.StringBuilder();
            await foreach (var piece in client.TranslateStreamAsync(req, s, _testCts.Token))
                sb.Append(piece);
            TestResult.Foreground = System.Windows.Media.Brushes.SeaGreen;
            TestResult.Text = "✓ 连接成功：" + sb.ToString().Trim();
        }
        catch (TranslationException ex)
        {
            TestResult.Foreground = System.Windows.Media.Brushes.Crimson;
            TestResult.Text = "✗ " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            TestResult.Foreground = System.Windows.Media.Brushes.Crimson;
            TestResult.Text = "✗ 测试超时";
        }
        catch (Exception ex)
        {
            TestResult.Foreground = System.Windows.Media.Brushes.Crimson;
            TestResult.Text = "✗ " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ---------------- 快捷键录制 ----------------

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _host.SuspendHotkey(true);
        HotkeyHint.Text = "请按下新的组合键…（Esc 取消，Backspace 恢复默认）";
    }

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _host.SuspendHotkey(false);
        HotkeyHint.Text = "点击输入框后按下新的组合键（需包含 Ctrl / Alt / Shift / Win 之一）";
        HotkeyBox.Text = _hotkey.ToString();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            HotkeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            _hotkey = HotkeyGesture.Default;
            HotkeyBox.Text = _hotkey.ToString();
            return;
        }

        var mods = Keyboard.Modifiers;
        if (HotkeyGesture.IsModifierKey(key))
        {
            // 只按了修饰键：显示预览
            HotkeyBox.Text = mods != ModifierKeys.None ? ModsPreview(mods) : "";
            return;
        }

        var gesture = new HotkeyGesture(mods, key);
        if (!gesture.IsValid)
        {
            HotkeyBox.Text = key.ToString();
            HotkeyHint.Text = "需要至少一个修饰键（Ctrl / Alt / Shift / Win）";
            return;
        }
        _hotkey = gesture;
        HotkeyBox.Text = gesture.ToString();
        HotkeyHint.Text = "已设置，保存后生效";
    }

    private static string ModsPreview(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _hotkey = HotkeyGesture.Default;
        HotkeyBox.Text = _hotkey.ToString();
    }

    // ---------------- 保存 ----------------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var s = BuildSettings();
        if (!Uri.TryCreate(DeepSeekClient.BuildEndpoint(s.BaseUrl), UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            MessageBox.Show(this, "Base URL 格式不正确，例如：https://api.deepseek.com", "划词翻译",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_host.TryApply(s, out var error))
        {
            MessageBox.Show(this, error ?? "应用设置失败", "划词翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _service.Save(s);
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
            MessageBox.Show(this, "保存设置失败：" + ex.Message, "划词翻译", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Close();
    }

    // ---------------- 其他 ----------------

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenShell(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsService.AppDataDir);
        OpenShell(SettingsService.AppDataDir);
    }

    private static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("打开失败：" + ex.Message); }
    }
}
