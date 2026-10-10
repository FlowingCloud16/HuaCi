using System.Text.Json.Serialization;

namespace HuaCi.Services;

/// <summary>用户设置，以 JSON 保存在 %AppData%\HuaCi\settings.json。</summary>
public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultHotkey = "Ctrl+Alt+D";
    public const string DefaultScreenshotHotkey = "Ctrl+Alt+Q";

    /// <summary>DPAPI（CurrentUser）加密后的 API Key，Base64。</summary>
    public string? ApiKeyProtected { get; set; }

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>模型 ID。留空时自动使用 GET /models 返回的第一个模型。</summary>
    public string Model { get; set; } = "";

    /// <summary>是否开启 DeepSeek 思考模式（更准确但更慢）。默认关闭以保证翻译速度。</summary>
    public bool EnableThinking { get; set; }

    /// <summary>目标语言：所有外语原文都翻译成这个语言。</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>可选：原文是中文时改为翻译成 <see cref="TargetLanguageForChinese"/>（默认关闭）。</summary>
    public bool TranslateChineseToOther { get; set; }

    public string TargetLanguageForChinese { get; set; } = "English";

    /// <summary>划词后在鼠标旁显示翻译按钮（主要交互方式，默认开启）。</summary>
    public bool EnableSelectIcon { get; set; } = true;

    /// <summary>可选的全局快捷键（默认关闭）。</summary>
    public bool EnableHotkey { get; set; }

    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>截图翻译快捷键（默认开启）。</summary>
    public bool EnableScreenshotHotkey { get; set; } = true;

    public string ScreenshotHotkey { get; set; } = DefaultScreenshotHotkey;

    /// <summary>截图识别方式："deepseek"（DeepSeek 识图，默认）或 "ocr"（Windows 本地 OCR）。</summary>
    public string ScreenshotEngine { get; set; } = ScreenshotEngines.DeepSeek;

    /// <summary>识图模型。留空 = 自动（主模型支持图片则用主模型，否则用 /models 中第一个支持图片的模型）。</summary>
    public string VisionModel { get; set; } = "";

    /// <summary>识图翻译时是否同时让模型输出识别出的原文（显示在弹窗中，可编辑重译）。</summary>
    public bool ShowRecognizedSource { get; set; } = true;

    /// <summary>OCR 语言标签（如 zh-Hans-CN、en-US）。留空 = 自动。</summary>
    public string OcrLanguage { get; set; } = "";

    public bool AutoStart { get; set; }

    /// <summary>弹窗默认是否展开原文。</summary>
    public bool ShowSourceExpanded { get; set; }

    public bool FirstRunTipShown { get; set; }

    /// <summary>设置文件格式版本，用于从旧版本迁移。</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    public const int CurrentVersion = 3;

    [JsonIgnore]
    public string ApiKey
    {
        get => SecretProtector.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = SecretProtector.Protect(value);
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public static class ScreenshotEngines
{
    public const string DeepSeek = "deepseek";
    public const string WindowsOcr = "ocr";
}
