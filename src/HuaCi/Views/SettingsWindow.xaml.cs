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
    private static readonly string[] Targets = ["简体中文", "繁體中文", "English", "日本語", "한국어"];
    private static readonly string[] ZhTargets = ["English", "日本語", "한국어", "Français", "Deutsch", "Español", "Русский", "繁體中文"];

    private readonly SettingsService _service;
    private readonly ISettingsHost _host;
    private HotkeyGesture _hotkey;
    private CancellationTokenSource? _testCts;
    private CancellationTokenSource? _modelsCts;
    private readonly System.Windows.Threading.DispatcherTimer _keyDebounce = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _loading;

    public SettingsWindow(SettingsService service, ISettingsHost host)
    {
        InitializeComponent();
        _service = service;
        _host = host;
        foreach (var t in Targets) TargetBox.Items.Add(t);
        foreach (var t in ZhTargets) TargetZhBox.Items.Add(t);
        _loading = true;
        LoadFrom(service.Current);
        _loading = false;
        _keyDebounce.Tick += async (_, _) => { _keyDebounce.Stop(); await FetchModelsAsync(auto: true); };
        Loaded += async (_, _) =>
        {
            if (CurrentApiKey.Length > 0) await FetchModelsAsync(auto: true);
        };
        Closed += (_, _) =>
        {
            _testCts?.Cancel();
            _modelsCts?.Cancel();
            _keyDebounce.Stop();
            _host.SuspendHotkey(false);
        };
    }

    private void LoadFrom(AppSettings s)
    {
        ApiKeyBox.Password = s.ApiKey;
        BaseUrlBox.Text = s.BaseUrl;
        ModelBox.Text = s.Model;
        ThinkingCheck.IsChecked = s.EnableThinking;
        TargetBox.Text = s.TargetLanguage;
        ZhToOtherCheck.IsChecked = s.TranslateChineseToOther;
        TargetZhBox.Text = s.TargetLanguageForChinese;
        HotkeyCheck.IsChecked = s.EnableHotkey;
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
        s.Model = (ModelBox.Text ?? "").Trim(); // 留空：翻译时自动使用 /models 的第一个
        s.EnableThinking = ThinkingCheck.IsChecked == true;
        s.TargetLanguage = string.IsNullOrWhiteSpace(TargetBox.Text) ? "简体中文" : TargetBox.Text.Trim();
        s.TranslateChineseToOther = ZhToOtherCheck.IsChecked == true;
        s.TargetLanguageForChinese = string.IsNullOrWhiteSpace(TargetZhBox.Text) ? "English" : TargetZhBox.Text.Trim();
        s.EnableHotkey = HotkeyCheck.IsChecked == true;
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

    // ---------------- 模型列表 ----------------

    private void ApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _keyDebounce.Stop();
        if (CurrentApiKey.Length >= 10) _keyDebounce.Start(); // 输入停顿后自动获取
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await FetchModelsAsync(auto: false);

    /// <summary>GET {baseUrl}/models 并填充下拉框。返回是否成功。</summary>
    private async Task<bool> FetchModelsAsync(bool auto)
    {
        var key = CurrentApiKey;
        if (key.Length == 0)
        {
            SetModelStatus("请先填写 API Key", error: !auto);
            return false;
        }

        _modelsCts?.Cancel();
        _modelsCts = new CancellationTokenSource();
        var ct = _modelsCts.Token;
        RefreshModelsButton.IsEnabled = false;
        SetModelStatus("正在获取模型列表…", error: false);
        try
        {
            using var client = new DeepSeekClient();
            var models = await client.ListModelsAsync(BaseUrlBox.Text, key, ct);
            if (ct.IsCancellationRequested) return false;

            var current = (ModelBox.Text ?? "").Trim();
            ModelBox.Items.Clear();
            foreach (var m in models)
                ModelBox.Items.Add(new ComboBoxItem
                {
                    Content = m.Id,
                    ToolTip = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name,
                });

            if (models.Count == 0)
            {
                ModelBox.Text = current;
                SetModelStatus("接口没有返回模型，请手动填写模型名称", error: true);
                return false;
            }

            // 未选择模型时默认选第一个；已有选择则保留（即使不在列表中，也允许手动填写）
            ModelBox.Text = current.Length > 0 ? current : models[0].Id;
            bool inList = models.Any(m => string.Equals(m.Id, ModelBox.Text, StringComparison.Ordinal));
            SetModelStatus(inList
                ? $"已获取 {models.Count} 个模型：{string.Join("、", models.Select(m => m.Id))}"
                : $"注意：当前模型“{ModelBox.Text}”不在接口返回的列表中（{string.Join("、", models.Select(m => m.Id))}）",
                error: !inList);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TranslationException ex)
        {
            SetModelStatus("获取模型列表失败：" + ex.Message, error: true);
            return false;
        }
        catch (Exception ex)
        {
            SetModelStatus("获取模型列表失败：" + ex.Message, error: true);
            return false;
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private void SetModelStatus(string text, bool error)
    {
        ModelStatus.Text = text;
        ModelStatus.Foreground = error ? System.Windows.Media.Brushes.Crimson : (System.Windows.Media.Brush)FindResource("SubtleBrush");
    }

    // ---------------- 测试连接 ----------------

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        TestButton.IsEnabled = false;
        TestResult.Visibility = Visibility.Visible;
        TestResult.Foreground = (System.Windows.Media.Brush)FindResource("SubtleBrush");
        TestResult.Text = "正在测试…";
        try
        {
            // 测试时顺便刷新模型列表；未选择模型时自动选第一个
            bool fetched = await FetchModelsAsync(auto: false);
            if (string.IsNullOrWhiteSpace(ModelBox.Text))
            {
                TestResult.Foreground = System.Windows.Media.Brushes.Crimson;
                TestResult.Text = fetched ? "✗ 请先选择模型" : "✗ 无法获取模型列表，请检查 API Key / Base URL，或手动填写模型名称";
                return;
            }
            var s = BuildSettings();
            using var client = new DeepSeekClient();
            var req = PromptBuilder.Build("Hello, world!", s);
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in client.TranslateStreamAsync(req, s, _testCts.Token))
                if (!chunk.IsReasoning) sb.Append(chunk.Text);
            TestResult.Foreground = System.Windows.Media.Brushes.SeaGreen;
            TestResult.Text = $"✓ 连接成功（{s.Model}）：" + sb.ToString().Trim();
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
        HotkeyHint.Text = "可选。选中文字后按快捷键翻译（需包含 Ctrl / Alt / Shift / Win 之一）";
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
