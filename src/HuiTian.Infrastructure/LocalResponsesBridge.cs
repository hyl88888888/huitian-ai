using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed class ModelRequestEventArgs(Guid requestId, TimeSpan elapsed) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public TimeSpan Elapsed { get; } = elapsed;
}

/// <summary>
/// Keeps the official App Server Responses protocol intact while normalizing
/// message ordering for the local chat template used by the HuiTian brain.
/// </summary>
public sealed class LocalResponsesBridge : IAsyncDisposable
{
    public const int DefaultPort = 18181;
    internal const int DirectAnswerThinkingBudgetTokens = 32;
    internal const int CompositeThinkingBudgetTokens = 48;
    internal const int TaskThinkingBudgetTokens = 64;

    private readonly HuiTianPaths _paths;
    private readonly Uri _backendBaseUri;
    private readonly int _listenPort;
    private readonly HttpListener _listener = new();
    private readonly HttpClient _backendClient;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public LocalResponsesBridge(
        HuiTianPaths paths,
        Uri backendBaseUri,
        int listenPort = DefaultPort)
    {
        if (listenPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(listenPort));
        }

        _paths = paths;
        _backendBaseUri = new Uri(backendBaseUri, "../");
        _listenPort = listenPort;
        _backendClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _listener.Prefixes.Add($"http://127.0.0.1:{_listenPort}/");
    }

    public Uri ResponsesBaseUri => new($"http://127.0.0.1:{_listenPort}/v1/");

    // Count every actual local-model request, including each tool follow-up.
    // Counting only the initial user turn made long agent loops look like two
    // model calls even when they generated eight separate responses.
    public event EventHandler<ModelRequestEventArgs>? ModelRequestStarted;
    public event EventHandler<ModelRequestEventArgs>? ModelRequestProgress;
    public event EventHandler<ModelRequestEventArgs>? ModelRequestFinished;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener.IsListening)
        {
            return Task.CompletedTask;
        }

        _paths.EnsureDirectories();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _listener.Close();
        _backendClient.Dispose();
        _lifetime?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var modelRequestId = Guid.Empty;
        Stopwatch? modelRequestTimer = null;
        CancellationTokenSource? progressCancellation = null;
        Task? progressTask = null;
        try
        {
            var relativePath = context.Request.Url?.PathAndQuery.TrimStart('/')
                               ?? string.Empty;
            var target = new Uri(_backendBaseUri, relativePath);
            using var request = new HttpRequestMessage(
                new HttpMethod(context.Request.HttpMethod), target);

            if (context.Request.HasEntityBody)
            {
                await using var input = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                var body = input.ToArray();
                if (string.Equals(
                        context.Request.Url?.AbsolutePath,
                        "/v1/responses",
                        StringComparison.OrdinalIgnoreCase))
                {
                    body = NormalizeResponsesRequest(body);
                    var tracePath = Path.Combine(
                        _paths.AppState,
                        "last-local-responses-request.json");
                    await File.WriteAllBytesAsync(tracePath, body, cancellationToken)
                        .ConfigureAwait(false);
                    modelRequestId = Guid.NewGuid();
                    modelRequestTimer = Stopwatch.StartNew();
                    progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    try
                    {
                        ModelRequestStarted?.Invoke(this,
                            new ModelRequestEventArgs(modelRequestId, TimeSpan.Zero));
                    }
                    catch
                    {
                        // Performance/UI observers must not prevent generation.
                    }
                    progressTask = ReportModelProgressAsync(modelRequestId,
                        modelRequestTimer, progressCancellation.Token);
                }

                request.Content = new ByteArrayContent(body);
                if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                        context.Request.ContentType);
                }
            }

            if (!string.IsNullOrWhiteSpace(context.Request.Headers["Accept"]))
            {
                request.Headers.TryAddWithoutValidation(
                    "Accept", context.Request.Headers["Accept"]);
            }

            using var upstreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            using var response = await _backendClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    upstreamCancellation.Token)
                .ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            // Always proxy chunked.  This lets us emit a standards-compliant
            // keepalive before the first backend byte, detect a caller that
            // has cancelled, and then cancel the expensive local generation
            // instead of leaving it queued behind the user's next task.
            context.Response.SendChunked = true;
            context.Response.KeepAlive = false;
            if (response.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }

            if (response.Headers.CacheControl is not null)
            {
                context.Response.Headers["Cache-Control"] =
                    response.Headers.CacheControl.ToString();
            }

            await CopyResponseWithDisconnectDetectionAsync(
                    response,
                    context.Response,
                    upstreamCancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during product shutdown.
        }
        catch (Exception exception)
        {
            try
            {
                var error = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        message = "回天本地核心暂时无法处理该请求。",
                        type = "local_bridge_error",
                        detail = exception.Message,
                    },
                }));
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = error.Length;
                await context.Response.OutputStream.WriteAsync(error, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The caller may already have disconnected.
            }
        }
        finally
        {
            if (progressCancellation is not null)
            {
                await progressCancellation.CancelAsync().ConfigureAwait(false);
                if (progressTask is not null)
                {
                    try { await progressTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                progressCancellation.Dispose();
                modelRequestTimer?.Stop();
                try
                {
                    ModelRequestFinished?.Invoke(this,
                        new ModelRequestEventArgs(modelRequestId,
                            modelRequestTimer?.Elapsed ?? TimeSpan.Zero));
                }
                catch
                {
                    // Completion telemetry must not change the model result.
                }
            }
            context.Response.OutputStream.Close();
            context.Response.Close();
        }
    }

    private async Task ReportModelProgressAsync(
        Guid requestId,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    ModelRequestProgress?.Invoke(this,
                        new ModelRequestEventArgs(requestId, timer.Elapsed));
                }
                catch
                {
                    // The task must continue even if its progress UI closes.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The model request completed or the user stopped the task.
        }
    }

    private static async Task CopyResponseWithDisconnectDetectionAsync(
        HttpResponseMessage upstream,
        HttpListenerResponse downstream,
        CancellationTokenSource upstreamCancellation)
    {
        await using var input = await upstream.Content
            .ReadAsStreamAsync(upstreamCancellation.Token)
            .ConfigureAwait(false);
        var output = downstream.OutputStream;
        var buffer = new byte[32 * 1024];
        var isEventStream = upstream.Content.Headers.ContentType?.MediaType?.Equals(
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase) == true;
        var hasForwardedContent = false;

        try
        {
            while (true)
            {
                var readTask = input.ReadAsync(buffer, upstreamCancellation.Token).AsTask();
                while (!readTask.IsCompleted)
                {
                    await Task.WhenAny(
                            readTask,
                            Task.Delay(TimeSpan.FromSeconds(2), upstreamCancellation.Token))
                        .ConfigureAwait(false);
                    if (readTask.IsCompleted)
                    {
                        break;
                    }

                    // SSE comments are legal between events.  For a normal
                    // JSON response only leading whitespace is legal, so stop
                    // heartbeats as soon as the first content byte arrives.
                    if (isEventStream || !hasForwardedContent)
                    {
                        var keepAlive = isEventStream
                            ? ": huitian-keepalive\n\n"u8.ToArray()
                            : " \n"u8.ToArray();
                        await output.WriteAsync(keepAlive, upstreamCancellation.Token)
                            .ConfigureAwait(false);
                        await output.FlushAsync(upstreamCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }

                var read = await readTask.ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hasForwardedContent = true;
                await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        upstreamCancellation.Token)
                    .ConfigureAwait(false);
                await output.FlushAsync(upstreamCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // A failed downstream write usually means that App Server has
            // interrupted the turn.  Propagate that cancellation upstream so
            // llama-server releases its only generation slot immediately.
            upstreamCancellation.Cancel();
            throw;
        }
    }

    internal static byte[] NormalizeResponsesRequest(byte[] body)
    {
        var root = JsonNode.Parse(body)?.AsObject()
                   ?? throw new JsonException("本地请求格式无效。");
        NormalizeReasoningEffort(root);
        var instructions = root["instructions"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            MergeInstructionsIntoFirstDeveloperMessage(root, instructions);
            root.Remove("instructions");
        }

        // Codex App Server can return a dynamic tool result as a mixed
        // text/image array. The local Responses backend requires
        // function_call_output.output to be plain input text. Keep the text
        // on the tool item and move only the newest visual result into a
        // regular user image message so the vision model can still inspect it.
        NormalizeDynamicToolOutputs(root);
        PruneHistoricalImages(root, 2);
        CompactHistoricalObservationOutputs(root);

        // Context checkpoints are housekeeping, not another open-ended agent
        // answer. On a local CPU/GPU model an unconstrained checkpoint can
        // spend minutes generating prose while the desktop remains idle.
        if (IsContextCheckpoint(root))
        {
            root["max_output_tokens"] = 192;
        }

        var route = FindDeclaredRoute(root);
        // Keep quick conversation responsive without reducing the reasoning
        // available to file, browser, presentation, or computer-control work.
        // The backend supports a per-request budget when no command-line
        // budget overrides it.
        root["thinking_budget_tokens"] = route == AgentTaskRoute.DirectAnswer
            ? DirectAnswerThinkingBudgetTokens
            : route == AgentTaskRoute.Composite ? CompositeThinkingBudgetTokens
            : TaskThinkingBudgetTokens;
        // Product turns never expose a hidden reasoning transcript.  Apply
        // this at the HTTP boundary on every request, including follow-up
        // function-call-output requests where the route marker can be absent
        // after App Server compaction.  Relying on the marker here allowed a
        // later tool step to silently re-enable the model's thinking template.
        root["chat_template_kwargs"] = new JsonObject
        {
            ["enable_thinking"] = false,
        };

        if (route is not null || HasProductTool(root))
        {
            // The App Server still enforces its real sandbox and approval
            // policy.  Its generic permission primer duplicates the product
            // contract and costs a large local-model prefill on every tool
            // turn, so keep enforcement but remove the repeated prose.
            RemovePermissionGuidanceFromDeveloperMessages(root);
        }

        var useUniversalExecutor = false;
        if (root["tools"] is JsonArray tools)
        {
            useUniversalExecutor = tools.OfType<JsonObject>().Any(tool =>
                string.Equals(
                    tool["name"]?.GetValue<string>(),
                    "huitian_execute",
                    StringComparison.Ordinal));
            for (var index = tools.Count - 1; index >= 0; index--)
            {
                if (tools[index] is not JsonObject tool ||
                    !string.Equals(
                        tool["type"]?.GetValue<string>(),
                        "function",
                        StringComparison.Ordinal) ||
                    !IsProductTool(tool["name"]?.GetValue<string>()) ||
                    useUniversalExecutor && !string.Equals(
                        tool["name"]?.GetValue<string>(),
                        "huitian_execute",
                        StringComparison.Ordinal) ||
                    IsModelLifecycleTool(tool["name"]?.GetValue<string>()) ||
                    route is { } declaredRoute &&
                    !ConversationPolicy.IsToolAllowed(
                        tool["name"]?.GetValue<string>() ?? string.Empty,
                        declaredRoute))
                {
                    tools.RemoveAt(index);
                }
            }
        }

        // The universal route must make one structured semantic choice before
        // it can end: respond for a true conversation result, or a concrete
        // capability with a declared final-deliverable contract.  Follow-up
        // requests after a function result remain automatic so multi-step
        // tasks can either continue with another capability or close.
        if (useUniversalExecutor && route == AgentTaskRoute.Composite &&
            !ContainsFunctionCallOutput(root["input"]))
        {
            root["tool_choice"] = new JsonObject
            {
                ["type"] = "function",
                ["name"] = "huitian_execute",
            };
        }

        if (useUniversalExecutor)
        {
            // Product adapters already verify their own results. Prevent the
            // local model from generating create + redundant read in one long
            // response before the first visible action. Dependent paths such
            // as create-then-send must also wait for real runtime output.
            root["parallel_tool_calls"] = false;
        }

        root.Remove("include");
        root.Remove("client_metadata");
        root.Remove("prompt_cache_key");
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static bool HasProductTool(JsonObject root) =>
        root["tools"] is JsonArray tools &&
        tools.OfType<JsonObject>().Any(tool =>
            IsProductTool(tool["name"]?.GetValue<string>()));

    // App Server still owns the public lifecycle definitions for backwards
    // compatibility, but the local model must not spend a full generation on
    // bookkeeping before and after every real action.  The client derives the
    // semantic contract from the first concrete structured tool selection and
    // closes it from recorded execution evidence when the turn completes.
    private static bool IsModelLifecycleTool(string? name) => name is
        "huitian_begin_task" or "huitian_finish_task";

    private static void NormalizeReasoningEffort(JsonObject root)
    {
        if (root["reasoning"] is not JsonObject reasoning ||
            reasoning["effort"] is not JsonValue effortValue ||
            !effortValue.TryGetValue<string>(out var effort))
        {
            return;
        }

        // The local Qwen3.8 template accepts low, medium, and xhigh.  Keep a
        // protocol-level guard here as well as in the App Server client so an
        // old session or another compatible caller cannot reintroduce the
        // deterministic "Unexpected reasoning effort high" failure.
        reasoning["effort"] = CodexAppServerClient.NormalizeReasoningEffort(effort);
    }

    private static void RemovePermissionGuidanceFromDeveloperMessages(JsonObject root)
    {
        if (root["input"] is not JsonArray input)
        {
            return;
        }

        foreach (var developer in input.OfType<JsonObject>().Where(item =>
                     string.Equals(
                         item["role"]?.GetValue<string>(),
                         "developer",
                         StringComparison.Ordinal)))
        {
            if (developer["content"] is not JsonArray content)
            {
                continue;
            }

            foreach (var item in content.OfType<JsonObject>().Where(IsTextContent))
            {
                var text = item["text"]?.GetValue<string>();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                item["text"] = Regex.Replace(
                        text,
                        @"(?is)\s*<permissions instructions>.*?</permissions instructions>\s*",
                        "\n")
                    .Trim();
            }
        }
    }

    private static AgentTaskRoute? FindDeclaredRoute(JsonObject root)
    {
        if (root["input"] is not JsonArray input)
        {
            return null;
        }

        AgentTaskRoute? route = null;
        foreach (var text in EnumerateText(input))
        {
            route = ConversationPolicy.FindDeclaredRoute(text) ?? route;
        }

        return route;
    }

    private static IEnumerable<string> EnumerateText(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value)
                {
                    if (property.Key is "text" or "output" &&
                        property.Value is JsonValue textValue &&
                        textValue.TryGetValue<string>(out var text))
                    {
                        yield return text;
                    }
                    else
                    {
                        foreach (var nested in EnumerateText(property.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var nested in EnumerateText(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static bool ContainsFunctionCallOutput(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                if (string.Equals(
                        value["type"]?.GetValue<string>(),
                        "function_call_output",
                        StringComparison.Ordinal))
                {
                    return true;
                }

                return value.Any(property => ContainsFunctionCallOutput(property.Value));
            case JsonArray array:
                return array.Any(ContainsFunctionCallOutput);
            default:
                return false;
        }
    }

    private static bool IsProductTool(string? name) => name is
        "huitian_execute" or
        "huitian_respond" or
        "huitian_begin_task" or
        "huitian_finish_task" or
        "huitian_search_files" or
        "huitian_read_text_file" or
        "huitian_read_document" or
        "huitian_write_text_file" or
        "huitian_create_document" or
        "huitian_open_application" or
        "huitian_open_browser" or
        "huitian_search_web" or
        "huitian_move_file" or
        "huitian_inspect_windows" or
        "huitian_activate_window" or
        "huitian_click_control" or
        "huitian_type_text" or
        "huitian_scroll_window" or
        "huitian_capture_screen" or
        "huitian_click_point" or
        "huitian_type_active" or
        "huitian_press_keys" or
        "huitian_send_chat_message" or
        "huitian_send_chat_files" or
        "huitian_create_presentation" or
        "update_plan";

    private static void MergeInstructionsIntoFirstDeveloperMessage(
        JsonObject root,
        string instructions)
    {
        if (root["input"] is not JsonArray input)
        {
            var original = root["input"]?.DeepClone();
            input = [];
            root["input"] = input;
            if (original is not null)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = original,
                        },
                    },
                });
            }
        }

        var developer = input
            .OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(
                item["role"]?.GetValue<string>(),
                "developer",
                StringComparison.Ordinal));
        if (developer is null)
        {
            developer = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "developer",
                ["content"] = new JsonArray(),
            };
            input.Insert(0, developer);
        }

        if (developer["content"] is not JsonArray content)
        {
            var existing = developer["content"]?.DeepClone();
            content = [];
            developer["content"] = content;
            if (existing is not null)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = existing,
                });
            }
        }

        var matchingInstructionItems = content
            .OfType<JsonObject>()
            .Where(item => IsTextContent(item) &&
                           string.Equals(
                               item["text"]?.GetValue<string>()?.Trim(),
                               instructions.Trim(),
                               StringComparison.Ordinal))
            .ToArray();
        if (matchingInstructionItems.Length == 0)
        {
            content.Insert(0, new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = instructions,
            });
        }
        else
        {
            foreach (var duplicate in matchingInstructionItems.Skip(1))
            {
                content.Remove(duplicate);
            }
        }
    }

    private static void NormalizeDynamicToolOutputs(JsonObject root)
    {
        if (root["input"] is not JsonArray input)
        {
            return;
        }

        var visualResults = new List<(int Index, JsonArray Images)>();
        for (var index = 0; index < input.Count; index++)
        {
            if (input[index] is not JsonObject item ||
                !string.Equals(
                    item["type"]?.GetValue<string>(),
                    "function_call_output",
                    StringComparison.Ordinal) ||
                item["output"] is not JsonArray output)
            {
                continue;
            }

            var textParts = new List<string>();
            var images = new JsonArray();
            foreach (var contentItem in output.OfType<JsonObject>())
            {
                if (IsTextContent(contentItem) &&
                    contentItem["text"]?.GetValue<string>() is { } text &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text.Trim());
                }
                else if (IsImageContent(contentItem))
                {
                    var imageUrl = contentItem["image_url"]?.GetValue<string>() ??
                                   contentItem["imageUrl"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        images.Add(new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = imageUrl,
                            ["detail"] = contentItem["detail"]?.DeepClone() ?? "high",
                        });
                    }
                }
            }

            item["output"] = textParts.Count == 0
                ? "本机操作已返回可视结果。"
                : string.Join("\n", textParts);
            if (images.Count > 0)
            {
                visualResults.Add((index, images));
            }
        }

        if (visualResults.Count == 0)
        {
            return;
        }

        var latest = visualResults[^1];
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = "以下画面是紧接上一项本机操作后观察到的结果，请据此继续当前任务。",
            },
        };
        foreach (var image in latest.Images)
        {
            content.Add(image?.DeepClone());
        }

        input.Insert(latest.Index + 1, new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = content,
        });
    }

    private static void PruneHistoricalImages(JsonObject root, int keepLatest)
    {
        if (root["input"] is not JsonArray input)
        {
            return;
        }

        var images = new List<(JsonArray Parent, JsonNode Node)>();
        foreach (var message in input.OfType<JsonObject>())
        {
            if (message["content"] is not JsonArray content)
            {
                continue;
            }

            images.AddRange(content
                .Where(node => node is JsonObject item && IsImageContent(item))
                .Select(node => (content, node!)));
        }

        foreach (var image in images.Take(Math.Max(0, images.Count - keepLatest)))
        {
            image.Parent.Remove(image.Node);
        }
    }

    private static void CompactHistoricalObservationOutputs(JsonObject root)
    {
        if (root["input"] is not JsonArray input)
        {
            return;
        }

        var outputs = input.OfType<JsonObject>()
            .Where(item => string.Equals(
                item["type"]?.GetValue<string>(),
                "function_call_output",
                StringComparison.Ordinal))
            .ToArray();
        for (var index = 0; index < outputs.Length; index++)
        {
            var item = outputs[index];
            if (item["output"] is not JsonValue outputValue ||
                !outputValue.TryGetValue<string>(out var value) ||
                string.IsNullOrEmpty(value))
            {
                continue;
            }

            var isObservation = value.StartsWith("当前窗口：", StringComparison.Ordinal) ||
                                value.Contains("\"role\":", StringComparison.Ordinal) ||
                                value.StartsWith("已观察", StringComparison.Ordinal);
            if (isObservation && index < outputs.Length - 1)
            {
                // A previous accessibility snapshot is stale after the next
                // action. Keep its window identity, not hundreds of controls
                // that the model must reread on every tool follow-up.
                var firstLine = value.Split(['\r', '\n'], 2,
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "已观察窗口";
                item["output"] = firstLine.Length > 180
                    ? firstLine[..180] + "…（旧观察已折叠，请以最近画面为准）"
                    : firstLine + "（旧观察已折叠，请以最近画面为准）";
                continue;
            }

            var limit = index == outputs.Length - 1 ? 3_200 : 1_200;
            if (value.Length > limit)
            {
                // Preserve the beginning (tool status and identifiers) and
                // the final outcome; never truncate a current short result.
                var headLength = limit - 180;
                item["output"] = value[..headLength] +
                                 "\n…（较长的工具详情已折叠）…\n" +
                                 value[^140..];
            }
        }
    }

    private static bool IsContextCheckpoint(JsonObject root)
    {
        if (root["input"] is not JsonArray input)
        {
            return false;
        }

        var lastUser = input.OfType<JsonObject>()
            .LastOrDefault(item => string.Equals(
                item["role"]?.GetValue<string>(), "user", StringComparison.Ordinal));
        if (lastUser?["content"] is not JsonArray content)
        {
            return false;
        }

        return content.OfType<JsonObject>().Any(item =>
            item["text"]?.GetValue<string>()?.StartsWith(
                "You are performing a CONTEXT CHECKPOINT COMPACTION.",
                StringComparison.Ordinal) == true);
    }

    private static bool IsTextContent(JsonObject item) =>
        item["type"]?.GetValue<string>() is "input_text" or "inputText" or "output_text";

    private static bool IsImageContent(JsonObject item) =>
        item["type"]?.GetValue<string>() is "input_image" or "inputImage";
}
