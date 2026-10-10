using System.Text.Json.Serialization;

namespace HuaCi.Services;

/// <summary>用户设置，以 JSON 保存在 %AppData%\HuaCi\settings.json。</summary>
public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultModel = "deepseek-chat";

    /// <summary>DPAPI（CurrentUser）加密后的 API Key，Base64。</summary>
    public string? ApiKeyProtected { get; set; }

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = DefaultModel;

    /// <summary>DeepSeek 官方建议翻译场景使用 1.3。</summary>
    public double Temperature { get; set; } = 1.3;

    public string Hotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>原文主要是中文时翻译成的语言。</summary>
    public string TargetLanguageForChinese { get; set; } = "English";

    /// <summary>原文是其他语言时翻译成的语言。</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    public bool AutoStart { get; set; }

    /// <summary>划词后在鼠标旁显示小图标（默认关闭）。</summary>
    public bool EnableSelectIcon { get; set; }

    /// <summary>弹窗默认是否展开原文。</summary>
    public bool ShowSourceExpanded { get; set; }

    public bool FirstRunTipShown { get; set; }

    [JsonIgnore]
    public string ApiKey
    {
        get => SecretProtector.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = SecretProtector.Protect(value);
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
