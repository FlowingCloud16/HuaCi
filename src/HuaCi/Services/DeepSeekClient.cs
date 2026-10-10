using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuaCi.Services;

/// <summary>流式增量：正文或思考过程（reasoning_content）。</summary>
public readonly record struct StreamChunk(string Text, bool IsReasoning);

/// <summary>GET /models 返回的模型。</summary>
public sealed record ModelInfo(string Id, string? Name)
{
    public override string ToString() => Id;
}

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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HuaCi/0.3");
    }

    /// <summary>规范化 Base URL：去掉末尾的 / 和 /chat/completions。</summary>
    public static string NormalizeBase(string? baseUrl)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl) ? AppSettings.DefaultBaseUrl : baseUrl.Trim();
        b = b.TrimEnd('/');
        const string suffix = "/chat/completions";
        if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) b = b[..^suffix.Length];
        return b;
    }

    public static string BuildEndpoint(string? baseUrl) => NormalizeBase(baseUrl) + "/chat/completions";

    public static string BuildModelsEndpoint(string? baseUrl) => NormalizeBase(baseUrl) + "/models";

    private static Uri ValidateUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new TranslationException("Base URL 格式不正确，请在“设置”中检查。", needsSettings: true);
        return u;
    }

    /// <summary>获取可用模型列表（GET {baseUrl}/models）。</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string? baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new TranslationException("尚未设置 API Key。", needsSettings: true);

        using var msg = new HttpRequestMessage(HttpMethod.Get, ValidateUri(BuildModelsEndpoint(baseUrl)));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException("获取模型列表超时，请检查网络。");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"网络错误：无法连接到 {SafeHost(baseUrl)}。\n{ex.Message}", inner: ex);
        }

        using (resp)
        {
            var body = await SafeReadAsync(resp, ct);
            if (!resp.IsSuccessStatusCode) throw MapHttpError(resp.StatusCode, body);
            var list = new List<ModelInfo>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } sid)
                            list.Add(new ModelInfo(sid, m.TryGetProperty("name", out var n) ? n.GetString() : null));
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new TranslationException("模型列表格式无法识别：" + ex.Message, inner: ex);
            }
            return list;
        }
    }

    /// <summary>流式翻译，逐段返回增量（思考过程与正文分开标记）。</summary>
    public async IAsyncEnumerable<StreamChunk> TranslateStreamAsync(
        TranslationRequest request, AppSettings settings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new TranslationException("尚未设置 DeepSeek API Key，请在“设置”中填写。", needsSettings: true);

        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new TranslationException("尚未选择模型，请在“设置”中选择。", needsSettings: true);

        var body = new JsonObject
        {
            ["model"] = settings.Model.Trim(),
            ["stream"] = true,
            // DeepSeek 默认开启思考模式；翻译场景默认关闭以提升速度（思考模式下 temperature 无效，故不发送）
            ["thinking"] = new JsonObject { ["type"] = settings.EnableThinking ? "enabled" : "disabled" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = PromptBuilder.SystemPrompt(request) },
                new JsonObject { ["role"] = "user", ["content"] = request.Text },
            },
        };

        var endpoint = ValidateUri(BuildEndpoint(settings.BaseUrl));

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

                var (content, reasoning) = ParseDelta(data, out var apiError);
                if (apiError is not null) throw new TranslationException("DeepSeek 返回错误：" + apiError);
                // 是否为思考内容只看响应字段 reasoning_content，不依赖模型名称
                if (!string.IsNullOrEmpty(reasoning))
                    yield return new StreamChunk(reasoning, true);
                if (!string.IsNullOrEmpty(content))
                {
                    any = true;
                    yield return new StreamChunk(content, false);
                }
            }

            if (!any) throw new TranslationException("DeepSeek 没有返回任何内容，请重试。");
        }
    }

    private static (string? Content, string? Reasoning) ParseDelta(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                error = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                return (null, null);
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("delta", out var delta))
            {
                return (Str(delta, "content"), Str(delta, "reasoning_content"));
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的行
        }
        return (null, null);

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static TranslationException MapHttpError(HttpStatusCode code, string body)
    {
        var detail = ExtractErrorMessage(body);
        string suffix = string.IsNullOrEmpty(detail) ? "" : $"\n（{detail}）";
        return (int)code switch
        {
            400 => new TranslationException("请求被拒绝（400），请检查所选模型等设置。" + suffix, needsSettings: true),
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
