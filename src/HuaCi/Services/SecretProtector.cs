using System.Security.Cryptography;
using System.Text;

namespace HuaCi.Services;

/// <summary>使用 Windows DPAPI（当前用户范围）加密 API Key。</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HuaCi.DeepSeek.ApiKey.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 换了用户/机器，或数据损坏：视为未设置
            Log.Warn("API Key 解密失败：" + ex.Message);
            return "";
        }
    }
}
