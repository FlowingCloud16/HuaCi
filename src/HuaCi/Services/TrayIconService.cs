using System.Windows;
using WinForms = System.Windows.Forms;

namespace HuaCi.Services;

/// <summary>系统托盘图标（使用 WinForms NotifyIcon）。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _hotkeyItem;

    public event EventHandler? SettingsRequested;
    public event EventHandler? TranslateRequested;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler<bool>? PauseToggled;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9f),
            ShowImageMargin = false,
        };

        _hotkeyItem = new WinForms.ToolStripMenuItem("翻译剪贴板文字");
        _hotkeyItem.Click += (_, _) => TranslateRequested?.Invoke(this, EventArgs.Empty);

        var screenshotItem = new WinForms.ToolStripMenuItem("截图翻译");
        screenshotItem.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new WinForms.ToolStripMenuItem("设置");
        settingsItem.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _pauseItem = new WinForms.ToolStripMenuItem("暂停") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => PauseToggled?.Invoke(this, _pauseItem.Checked);

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.AddRange([settingsItem, _pauseItem, new WinForms.ToolStripSeparator(), screenshotItem, _hotkeyItem, new WinForms.ToolStripSeparator(), exitItem]);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "划词翻译",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) SettingsRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    public void UpdateState(string trigger, bool paused)
    {
        var text = paused ? "划词翻译（已暂停）" : $"划词翻译 - {trigger}";
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon.Text 上限 63 字符
        if (_pauseItem.Checked != paused) _pauseItem.Checked = paused;
    }

    public void ShowBalloon(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(4000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
