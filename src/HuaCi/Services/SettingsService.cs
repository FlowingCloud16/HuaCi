using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HuaCi.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义，便于手动查看
    };

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HuaCi");

    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Migrate(Current, json);
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，使用默认设置", ex);
            Current = new AppSettings();
        }
    }

    /// <summary>
    /// v0.1 → v0.2：划词按钮改为默认开启、快捷键改为默认关闭；
    /// v0.1 自动写入的旧默认模型名清空，改为从 /models 获取。
    /// </summary>
    private static void Migrate(AppSettings s, string json)
    {
        if (json.Contains("\"SettingsVersion\"", StringComparison.Ordinal) && s.SettingsVersion >= AppSettings.CurrentVersion)
            return;
        s.EnableSelectIcon = true;
        s.EnableHotkey = false;
        if (s.Model is "deepseek-chat") s.Model = "";
        s.SettingsVersion = AppSettings.CurrentVersion;
        Log.Info("设置已从旧版本迁移");
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDir);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, SettingsPath, overwrite: true);
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
