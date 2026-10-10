namespace HuaCi.Services;

/// <summary>带有面向用户的中文提示的翻译错误。</summary>
public sealed class TranslationException(string userMessage, bool needsSettings = false, Exception? inner = null)
    : Exception(userMessage, inner)
{
    /// <summary>是否需要用户去设置里修正（例如没有 Key、Key 无效）。</summary>
    public bool NeedsSettings { get; } = needsSettings;
}
