namespace HuaCi.Services;

/// <summary>简单的字符统计语言判断。</summary>
public static class LanguageDetector
{
    /// <summary>文本是否"主要是中文"：汉字占字母类字符的一半以上，且不含日文假名/韩文为主。</summary>
    public static bool IsMainlyChinese(string text)
    {
        int han = 0, kana = 0, hangul = 0, otherWords = 0;
        bool inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            bool letter = false;
            if (IsHan(v)) han++;
            else if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF) kana++;
            else if (v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul++;
            else if (System.Text.Rune.IsLetter(rune)) letter = true;
            if (letter && !inWord) otherWords++;
            inWord = letter;
        }
        if (han == 0) return false;
        // 含较多假名 → 日文；含较多谚文 → 韩文
        if (kana > han * 0.2 || hangul > han) return false;
        // 一个其他语言单词大约相当于 1.5 个汉字
        return han >= otherWords * 1.5;
    }

    private static bool IsHan(int v) =>
        v is >= 0x4E00 and <= 0x9FFF   // CJK 统一汉字
        or >= 0x3400 and <= 0x4DBF     // 扩展 A
        or >= 0x20000 and <= 0x2EBEF   // 扩展 B-F
        or >= 0xF900 and <= 0xFAFF;    // 兼容汉字

    /// <summary>是否像单个词（用于词典式释义）。</summary>
    public static bool IsSingleWord(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 40) return false;
        if (t.Any(char.IsWhiteSpace))
            return false;
        int han = t.EnumerateRunes().Count(r => IsHan(r.Value));
        if (han > 0) return han <= 4 && han == t.Length; // 短中文词语
        // 拉丁字母单词，允许连字符和撇号
        return t.All(c => char.IsLetter(c) || c is '-' or '\'' or '’');
    }
}
