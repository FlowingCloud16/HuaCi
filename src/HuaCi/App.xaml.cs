using System.Windows;
using System.Windows.Threading;
using HuaCi.Native;
using HuaCi.Services;
using HuaCi.Views;

namespace HuaCi;

public partial class App : Application, ISettingsHost
{
    private const string MutexName = @"Local\HuaCi.SingleInstance";
    private const string ShowSettingsEventName = @"Local\HuaCi.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _showSettingsEvent;
    private RegisteredWaitHandle? _showSettingsWait;

    private readonly SettingsService _settings = new();
    private HotkeyManager? _hotkeys;
    private TrayIconService? _tray;
    private TranslationController? _controller;
    private SelectIconWatcher? _selectIcon;
    private ScreenshotController? _screenshot;

    private const int HotkeyTranslate = 1;
    private const int HotkeyScreenshot = 2;
    private SettingsWindow? _settingsWindow;
    private bool _paused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 单实例：已在运行时通知旧实例打开设置窗口 ----
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowSettingsEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(_showSettingsEvent,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("未处理异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("未观察的任务异常", args.Exception); args.SetObserved(); };

        _settings.Load();
        Log.Info("启动 v" + typeof(App).Assembly.GetName().Version);

        _tray = new TrayIconService();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.PauseToggled += (_, paused) => SetPaused(paused);
        _tray.TranslateRequested += (_, _) => TranslateClipboard();
        _tray.ScreenshotRequested += (_, _) => StartScreenshot();
        _tray.ExitRequested += (_, _) => ExitApp();

        _controller = new TranslationController(_settings, OpenSettings);

        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += (_, id) =>
        {
            if (id == HotkeyScreenshot) StartScreenshot();
            else _ = _controller.TranslateSelectionAsync();
        };

        _selectIcon = new SelectIconWatcher(Dispatcher);
        _screenshot = new ScreenshotController(_settings, _controller, () =>
        {
            _selectIcon.HideIcon();
            _controller.CloseActivePopup();
        });
        _selectIcon.IconClicked += (_, _) => _ = _controller.TranslateSelectionAsync(silentIfEmpty: true);

        var s = _settings.Current;
        RegisterHotkeysFromSettings(s, showError: true);
        _selectIcon.SetEnabled(s.EnableSelectIcon);
        if (s.AutoStart) AutoStartService.RefreshPathIfEnabled();
        _tray.UpdateState(DescribeTrigger(s), false);

        bool launchedByAutoStart = e.Args.Contains(AutoStartService.AutoStartArg);
        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            _tray.ShowBalloon("划词翻译", "请先在设置中填写 DeepSeek API Key。");
            OpenSettings();
        }
        else if (!s.FirstRunTipShown || !launchedByAutoStart)
        {
            _tray.ShowBalloon("划词翻译已在后台运行", HowToUse(s) + "右键托盘图标可打开设置。");
        }

        if (!s.FirstRunTipShown)
        {
            var copy = s.Clone();
            copy.FirstRunTipShown = true;
            try { _settings.Save(copy); } catch (Exception ex) { Log.Warn("保存设置失败：" + ex.Message); }
        }
    }

    private void StartScreenshot()
    {
        if (_paused || _screenshot is null) return;
        _ = _screenshot.StartAsync();
    }

    /// <summary>按设置注册所有快捷键；关闭的快捷键会被注销。返回注册失败的描述（全部成功时为 null）。</summary>
    private string? RegisterHotkeysFromSettings(AppSettings s, bool showError)
    {
        var failures = new List<string>();
        void One(int id, bool enabled, string text, HotkeyGesture fallback, string name)
        {
            if (!enabled)
            {
                _hotkeys!.Unregister(id);
                return;
            }
            if (!HotkeyGesture.TryParse(text, out var g)) g = fallback;
            if (!_hotkeys!.Register(id, g))
            {
                Log.Warn($"{name}快捷键 {g} 注册失败");
                failures.Add($"{name}快捷键 {g}");
            }
        }
        One(HotkeyTranslate, s.EnableHotkey, s.Hotkey, HotkeyGesture.Default, "划词翻译");
        One(HotkeyScreenshot, s.EnableScreenshotHotkey, s.ScreenshotHotkey, HotkeyGesture.DefaultScreenshot, "截图翻译");
        if (failures.Count == 0) return null;
        var msg = string.Join("、", failures) + " 已被系统或其他程序占用，请在设置中换一个组合。";
        if (showError) _tray?.ShowBalloon("快捷键注册失败", msg, warning: true);
        return msg;
    }

    private static string HowToUse(AppSettings s)
    {
        var parts = new List<string>();
        if (s.EnableSelectIcon) parts.Add("选中文字后点击旁边的「翻译」按钮");
        if (s.EnableHotkey) parts.Add($"选中文字后按 {s.Hotkey}");
        var text = parts.Count == 0 ? "划词按钮和快捷键都已关闭，可在设置中开启。" : string.Join("，或", parts) + "即可翻译。";
        if (s.EnableScreenshotHotkey) text += $"按 {s.ScreenshotHotkey} 截图翻译。";
        return text;
    }

    private static string DescribeTrigger(AppSettings s) =>
        s.EnableHotkey ? (s.EnableSelectIcon ? $"划词 / {s.Hotkey}" : s.Hotkey) : (s.EnableSelectIcon ? "划词翻译已开启" : "触发方式已关闭");

    private void TranslateClipboard()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (!string.IsNullOrWhiteSpace(text))
                _controller?.Translate(text, ScreenHelper.GetCursorPos());
            else
                _tray?.ShowBalloon("划词翻译", "剪贴板中没有文字。");
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪贴板失败：" + ex.Message);
        }
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        var s = _settings.Current;
        if (paused)
        {
            _hotkeys?.UnregisterAll();
            _selectIcon?.SetEnabled(false);
            _controller?.CloseActivePopup();
        }
        else
        {
            RegisterHotkeysFromSettings(s, showError: true);
            _selectIcon?.SetEnabled(s.EnableSelectIcon);
        }
        _tray?.UpdateState(DescribeTrigger(s), paused);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---------------- ISettingsHost ----------------

    private bool _hotkeysSuspended;

    public void SuspendHotkey(bool suspend)
    {
        if (_paused || _hotkeys is null || suspend == _hotkeysSuspended) return;
        _hotkeysSuspended = suspend;
        if (suspend) _hotkeys.UnregisterAll();
        else RegisterHotkeysFromSettings(_settings.Current, showError: false);
    }

    public bool TryApply(AppSettings s, out string? error)
    {
        error = null;
        HotkeyGesture? g1 = null, g2 = null;
        if (s.EnableHotkey)
        {
            if (!HotkeyGesture.TryParse(s.Hotkey, out var g)) { error = "划词翻译快捷键无效。"; return false; }
            g1 = g;
        }
        if (s.EnableScreenshotHotkey)
        {
            if (!HotkeyGesture.TryParse(s.ScreenshotHotkey, out var g)) { error = "截图翻译快捷键无效。"; return false; }
            g2 = g;
        }
        if (g1 is not null && g1 == g2)
        {
            error = "划词翻译和截图翻译不能使用同一个快捷键。";
            return false;
        }

        if (!_paused)
        {
            _hotkeysSuspended = false;
            var failure = RegisterHotkeysFromSettings(s, showError: false);
            if (failure is not null)
            {
                RegisterHotkeysFromSettings(_settings.Current, showError: false); // 恢复旧快捷键
                error = failure;
                return false;
            }
        }

        try
        {
            AutoStartService.SetEnabled(s.AutoStart);
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            error = "设置开机自启失败：" + ex.Message;
            return false;
        }

        if (!_paused) _selectIcon?.SetEnabled(s.EnableSelectIcon);
        _tray?.UpdateState(DescribeTrigger(s), _paused);
        return true;
    }

    // ---------------- 退出与异常 ----------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);
        e.Handled = true; // 托盘程序尽量不要崩溃
    }

    private void ExitApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSettingsWait?.Unregister(null);
        _showSettingsEvent?.Dispose();
        _selectIcon?.Dispose();
        _controller?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
