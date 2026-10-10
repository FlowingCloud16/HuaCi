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
        _tray.ExitRequested += (_, _) => ExitApp();

        _controller = new TranslationController(_settings, OpenSettings);

        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += (_, _) => _ = _controller.TranslateSelectionAsync();

        _selectIcon = new SelectIconWatcher(Dispatcher);
        _selectIcon.IconClicked += (_, _) => _ = _controller.TranslateSelectionAsync(silentIfEmpty: true);

        var s = _settings.Current;
        RegisterHotkeyFromSettings(s, showError: true);
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

    /// <summary>按设置注册快捷键；快捷键关闭时注销。</summary>
    private void RegisterHotkeyFromSettings(AppSettings s, bool showError)
    {
        if (!s.EnableHotkey)
        {
            _hotkeys!.Unregister();
            return;
        }
        if (!HotkeyGesture.TryParse(s.Hotkey, out var g)) g = HotkeyGesture.Default;
        if (!_hotkeys!.Register(g) && showError)
        {
            Log.Warn($"热键 {g} 注册失败");
            _tray?.ShowBalloon("快捷键注册失败", $"{g} 可能已被其他程序占用，请在设置中更换。", warning: true);
        }
    }

    private static string HowToUse(AppSettings s)
    {
        var parts = new List<string>();
        if (s.EnableSelectIcon) parts.Add("选中文字后点击旁边的「翻译」按钮");
        if (s.EnableHotkey) parts.Add($"选中文字后按 {s.Hotkey}");
        return parts.Count == 0 ? "划词按钮和快捷键都已关闭，可在设置中开启。" : string.Join("，或", parts) + "即可翻译。";
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
            _hotkeys?.Unregister();
            _selectIcon?.SetEnabled(false);
            _controller?.CloseActivePopup();
        }
        else
        {
            RegisterHotkeyFromSettings(s, showError: true);
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

    public void SuspendHotkey(bool suspend)
    {
        if (_paused || _hotkeys is null) return;
        if (suspend) _hotkeys.Unregister();
        else if (_hotkeys.Current is null) RegisterHotkeyFromSettings(_settings.Current, showError: false);
    }

    public bool TryApply(AppSettings s, out string? error)
    {
        error = null;
        if (!HotkeyGesture.TryParse(s.Hotkey, out var g))
        {
            if (s.EnableHotkey)
            {
                error = "快捷键无效。";
                return false;
            }
            g = HotkeyGesture.Default;
        }

        if (!_paused && !s.EnableHotkey)
        {
            _hotkeys!.Unregister();
        }
        else if (!_paused)
        {
            if (!_hotkeys!.Register(g))
            {
                RegisterHotkeyFromSettings(_settings.Current, showError: false); // 恢复旧热键
                error = $"快捷键 {g} 已被系统或其他程序占用，请换一个组合。";
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
