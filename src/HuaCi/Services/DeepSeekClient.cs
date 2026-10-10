using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuaCi.Services;

/// <summary>DeepSeek（OpenAI 兼容）Chat Completions 流式客户端。</summary>
public sealed class DeepSeekClient : IDisposable
{
    private readonly HttpClient _http;

    public DeepSeekClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };
        // 流式响应总时长由调用方的 CancellationToken 控制
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HuaCi/0.1");
    }

    public static string BuildEndpoint(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? AppSettings.DefaultBaseUrl : baseUrl.Trim();
        b = b.TrimEnd('/');
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b + "/chat/completions";
    }

    /// <summary>流式翻译，逐段返回增量文本。</summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request, AppSettings settings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new TranslationException("尚未设置 DeepSeek API Key，请在“设置”中填写。", needsSettings: true);

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(settings.Model) ? AppSettings.DefaultModel : settings.Model.Trim(),
            ["stream"] = true,
            ["temperature"] = settings.Temperature,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = PromptBuilder.SystemPrompt(request) },
                new JsonObject { ["role"] = "user", ["content"] = request.Text },
            },
        };

        if (!Uri.TryCreate(BuildEndpoint(settings.BaseUrl), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            throw new TranslationException("Base URL 格式不正确，请在“设置”中检查。", needsSettings: true);

        using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // 首包超时 30 秒（DeepSeek 高峰期可能排队）
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(30));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException("连接 DeepSeek 超时，请检查网络或稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"网络错误：无法连接到 {SafeHost(settings.BaseUrl)}。\n{ex.Message}", inner: ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await SafeReadAsync(resp, ct);
                throw MapHttpError(resp.StatusCode, errText);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            bool any = false;

            while (true)
            {
                string? line;
                try
                {
                    // 两段数据之间最多等待 60 秒
                    using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lineCts.CancelAfter(TimeSpan.FromSeconds(60));
                    line = await reader.ReadLineAsync(lineCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TranslationException("DeepSeek 响应超时，请稍后重试。");
                }
                catch (IOException ex)
                {
                    throw new TranslationException("网络连接中断：" + ex.Message, inner: ex);
                }

                if (line is null) break;                // 流结束
                if (line.Length == 0 || line[0] == ':') continue; // 空行 / keep-alive 注释
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line.AsSpan(5).Trim().ToString();
                if (data == "[DONE]") break;

                string? piece = ParseDelta(data, out var apiError);
                if (apiError is not null) throw new TranslationException("DeepSeek 返回错误：" + apiError);
                if (!string.IsNullOrEmpty(piece))
                {
                    any = true;
                    yield return piece;
                }
            }

            if (!any) throw new TranslationException("DeepSeek 没有返回任何内容，请重试。");
        }
    }

    private static string? ParseDelta(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                error = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                return null;
            }
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    return content.GetString();
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的行
        }
        return null;
    }

    private static TranslationException MapHttpError(HttpStatusCode code, string body)
    {
        var detail = ExtractErrorMessage(body);
        string suffix = string.IsNullOrEmpty(detail) ? "" : $"\n（{detail}）";
        return (int)code switch
        {
            400 => new TranslationException("请求格式错误（400），请检查模型名称等设置。" + suffix, needsSettings: true),
            401 => new TranslationException("API Key 无效或已失效（401），请在“设置”中检查。" + suffix, needsSettings: true),
            402 => new TranslationException("DeepSeek 账户余额不足（402），请前往开放平台充值。" + suffix),
            403 => new TranslationException("没有访问权限（403）。" + suffix, needsSettings: true),
            404 => new TranslationException("接口地址或模型不存在（404），请检查 Base URL 和模型名称。" + suffix, needsSettings: true),
            422 => new TranslationException("请求参数错误（422）。" + suffix, needsSettings: true),
            429 => new TranslationException("请求过于频繁（429），请稍后再试。" + suffix),
            500 or 502 or 503 or 504 => new TranslationException($"DeepSeek 服务暂时不可用（{(int)code}），请稍后重试。" + suffix),
            _ => new TranslationException($"请求失败（HTTP {(int)code}）。" + suffix),
        };
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                if (e.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] + "…" : body;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string SafeHost(string? baseUrl)
    {
        try { return new Uri(BuildEndpoint(baseUrl)).Host; }
        catch { return baseUrl ?? ""; }
    }

    public void Dispose() => _http.Dispose();
}
