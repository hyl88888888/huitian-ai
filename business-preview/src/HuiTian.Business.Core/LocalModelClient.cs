using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HuiTian.Business.Core;

public sealed class ModelException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
public sealed record ModelSampling(double Temperature = 0.7, double TopP = 0.8, int TopK = 20, double PresencePenalty = 1.5);

/// <summary>Only a local, non-streaming OpenAI-compatible model endpoint is allowed.</summary>
public sealed class LocalModelClient : IModelClient, IStructuredModelClient, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string model;
    private readonly TimeSpan timeout;
    private readonly Func<string?> apiKeyProvider;
    private readonly ModelSampling sampling;

    public LocalModelClient(string endpoint = "http://127.0.0.1:18280/v1/chat/completions",
        string model = "huitian-business-local", TimeSpan? timeout = null, HttpMessageHandler? handler = null,
        Func<string?>? apiKeyProvider = null, ModelSampling? sampling = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("模型端点必须是本机回环 HTTP 地址。", nameof(endpoint));
        this.endpoint = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri : uri;
        this.model = model;
        this.sampling = sampling ?? new();
        if (!double.IsFinite(this.sampling.Temperature) || this.sampling.Temperature is < 0 or > 2 ||
            !double.IsFinite(this.sampling.TopP) || this.sampling.TopP is <= 0 or > 1 || this.sampling.TopK is < 0 or > 1000 ||
            !double.IsFinite(this.sampling.PresencePenalty) || this.sampling.PresencePenalty is < -2 or > 2)
            throw new ArgumentOutOfRangeException(nameof(sampling));
        this.apiKeyProvider = apiKeyProvider ?? (() =>
        {
            var keyFile = Path.Combine(BusinessPaths.Discover().Data, "local-model.key");
            return File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : null;
        });
        this.timeout = timeout ?? TimeSpan.FromSeconds(120);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    }

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, int maxTokens,
        CancellationToken cancellationToken) => CompleteCoreAsync(messages, json, null, maxTokens, cancellationToken);

    public Task<string> CompleteStructuredAsync(IReadOnlyList<ChatMessage> messages, JsonElement schema, int maxTokens,
        CancellationToken cancellationToken) => CompleteCoreAsync(messages, true, schema, maxTokens, cancellationToken);

    private async Task<string> CompleteCoreAsync(IReadOnlyList<ChatMessage> messages, bool json, JsonElement? schema, int maxTokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = false,
            // Qwen3.5 non-thinking defaults: avoid near-greedy repetitive degeneration.
            // Syntax is enforced independently by the native JSON schema grammar.
            ["temperature"] = sampling.Temperature,
            ["top_p"] = sampling.TopP,
            ["top_k"] = sampling.TopK,
            ["presence_penalty"] = sampling.PresencePenalty,
            ["max_tokens"] = Math.Clamp(maxTokens, 128, 8192),
            ["chat_template_kwargs"] = new { enable_thinking = false }
        };
        if (schema is not null)
            payload["response_format"] = new { type = "json_schema", json_schema = new { name = "huitian_task", strict = true, schema = schema.Value } };
        else if (json) payload["response_format"] = new { type = "json_object" };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(payload) };
            var key = apiKeyProvider();
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new ModelException("http_error", $"本地模型服务返回 HTTP {(int)response.StatusCode}。请检查服务状态和模型配置。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object ||
                !choices[0].TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
                throw new ModelException("empty_response", "本地模型没有返回可用内容，请检查非思考模式和上下文容量。");
            if (choices[0].TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                throw new ModelException("truncated_response", "本地模型输出达到长度上限，任务计划可能不完整，未执行该计划。");
            return content.GetString()!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelException("timeout", "本地模型响应超时。本次任务未继续执行，请检查模型服务后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException("unavailable", "无法连接本地模型服务。请先启动本地模型并确认端口 18280 可用。", ex);
        }
        catch (JsonException ex)
        {
            throw new ModelException("invalid_response", "本地模型服务返回了无效数据。", ex);
        }
    }

    public void Dispose() => client.Dispose();
}

public interface IStructuredModelClient : IModelClient
{
    Task<string> CompleteStructuredAsync(IReadOnlyList<ChatMessage> messages, JsonElement schema, int maxTokens,
        CancellationToken cancellationToken);
}
