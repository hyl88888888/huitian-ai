using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

/// <summary>
/// A compact semantic call that resolves the boundary between answering and
/// acting. For external communication it also extracts target slots in the
/// same model response, avoiding a second serial model request. It never
/// performs an action itself.
/// </summary>
public sealed partial class SemanticIntentRouter : IDisposable
{
    private const int MaximumCachedDecisions = 160;
    private const int MaximumDecisionTokens = 320;

    private readonly HttpClient _client;
    private readonly bool _useLocalJsonMode;
    private readonly TimeSpan _requestTimeout;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _diagnosticPath;
    private readonly ConcurrentDictionary<string, SemanticIntentDecision> _decisionCache =
        new(StringComparer.Ordinal);

    public SemanticIntentRouter(
        AgentBackendConfiguration configuration,
        HuiTianPaths paths)
        : this(configuration, paths, null)
    {
    }

    internal SemanticIntentRouter(
        AgentBackendConfiguration configuration,
        HuiTianPaths paths,
        HttpMessageHandler? handler,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        _model = configuration.ModelName;
        _useLocalJsonMode = configuration.IsProductDefault;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(
            configuration.IsProductDefault ? 35 : 18);
        // A lightweight request can use the already running native local
        // endpoint directly. This also propagates cancellation without waiting
        // for the Responses bridge to discover a disconnected JSON client.
        var baseUrl = configuration.IsProductDefault
            ? "http://127.0.0.1:18180/v1"
            : configuration.ResponsesBaseUrl.TrimEnd('/');
        _endpoint = new Uri(baseUrl + "/chat/completions");
        _diagnosticPath = Path.Combine(paths.AppState, "semantic-intent.jsonl");
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<SemanticIntentDecision?> ClassifyAsync(
        string currentRequest,
        IReadOnlyList<ConversationMessageSnapshot>? priorMessages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(currentRequest))
        {
            return null;
        }

        var context = BuildContext(priorMessages);
        var cacheKey = BuildCacheKey(currentRequest, context);
        if (_decisionCache.TryGetValue(cacheKey, out var cached))
        {
            var cacheHit = cached with
            {
                ElapsedMilliseconds = 0,
                Source = "local-semantic-cache",
            };
            WriteDiagnostic(cacheHit, timedOut: false);
            return cacheHit;
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        string? finishReason = null;
        int? contentLength = null;
        int? httpStatus = null;
        try
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = new object[]
                {
                    new
                    {
                        role = "system",
                        content = ClassificationInstructions,
                    },
                    new
                    {
                        role = "user",
                        content = string.IsNullOrWhiteSpace(context)
                            ? "当前要求：" + currentRequest.Trim()
                            : "最近对话：\n" + context + "\n当前要求：" + currentRequest.Trim(),
                    },
                },
                ["temperature"] = 0,
                // Normal routes emit a tiny JSON object. Chat routes include
                // recipient slots so no second model prefill is needed.
                ["max_tokens"] = MaximumDecisionTokens,
                ["stream"] = false,
                ["chat_template_kwargs"] = new
                {
                    enable_thinking = false,
                },
                ["cache_prompt"] = true,
            };
            if (_useLocalJsonMode)
            {
                body["response_format"] = new { type = "json_object" };
            }

            using var response = await _client.PostAsJsonAsync(
                    _endpoint,
                    body,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var failure = ReadCompletion(document.RootElement, out var content, out finishReason);
            contentLength = content?.Length;
            stopwatch.Stop();
            var decision = failure is null &&
                (!_useLocalJsonMode || content?.TrimStart().StartsWith('{') == true)
                ? ParseDecision(content, stopwatch.ElapsedMilliseconds, currentRequest)
                : null;
            failure ??= decision is null ? "invalid_decision" : null;
            cancellationToken.ThrowIfCancellationRequested();
            RememberDecision(cacheKey, decision);
            WriteDiagnostic(decision, timedOut: false, stopwatch.ElapsedMilliseconds,
                failure, finishReason, contentLength, httpStatus);
            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            WriteDiagnostic(null, timedOut: true, stopwatch.ElapsedMilliseconds,
                "timeout", finishReason, contentLength, httpStatus);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var failure = exception switch
            {
                JsonException => "invalid_json",
                HttpRequestException when httpStatus is >= 400 => "http_error",
                HttpRequestException => "transport_error",
                _ => "request_error",
            };
            WriteDiagnostic(null, timedOut: false, stopwatch.ElapsedMilliseconds,
                failure, finishReason, contentLength, httpStatus);
            return null;
        }
    }

    private static string? ReadCompletion(
        JsonElement root,
        out string? content,
        out string? finishReason)
    {
        content = null;
        finishReason = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object)
        {
            return "missing_choice";
        }

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var reason) &&
            reason.ValueKind != JsonValueKind.Null)
        {
            // Never log arbitrary server text: it may echo the user's input.
            finishReason = reason.ValueKind == JsonValueKind.String ? reason.GetString() switch
            {
                "stop" => "stop",
                "length" => "length",
                "content_filter" => "content_filter",
                "tool_calls" => "tool_calls",
                "function_call" => "function_call",
                _ => "other",
            } : "other";
        }

        if (!choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var text))
        {
            return "missing_content";
        }

        if (text.ValueKind == JsonValueKind.String)
        {
            content = text.GetString();
        }
        else if (text.ValueKind != JsonValueKind.Null)
        {
            return "invalid_content_type";
        }

        // Even a valid-looking leading code is incomplete when truncated.
        // Do not cache it or authorize an action from it.
        if (finishReason == "length") return "truncated_response";
        if (finishReason is "content_filter" or "tool_calls" or "function_call")
            return "unexpected_finish_reason";
        return string.IsNullOrWhiteSpace(content) ? "empty_content" : null;
    }

    internal static SemanticIntentDecision? ParseDecision(
        string? content,
        long elapsedMilliseconds = 0,
        string? currentRequest = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("d", out var codeNode) ||
                    codeNode.ValueKind != JsonValueKind.String)
                    return null;
                var code = codeNode.GetString();
                if (code is not { Length: 1 } || !"AWVBFPSRXMC".Contains(code[0]))
                    return null;
                var decision = ParseDecision(code, elapsedMilliseconds, currentRequest);
                if (decision is null ||
                    !root.TryGetProperty("comm", out var communication) ||
                    communication.ValueKind != JsonValueKind.Object ||
                    !CommunicationIntentResolver.TryParseTargetSlots(
                        communication.GetRawText(), out var slots))
                    return decision;
                // The compact model occasionally emits a system/browser code
                // alongside a complete communication structure. Trust the
                // validated semantic slots over that inconsistent one-letter
                // domain; otherwise social work loses its target state and
                // silently falls into the generic desktop agent. Never turn
                // an explicit answer or mixed multi-domain request into a
                // send action solely because a stray comm object appeared.
                if (decision.Domain != SemanticIntentDomain.Chat &&
                    decision.Mode == SemanticIntentMode.Operate)
                {
                    decision = decision with
                    {
                        Domain = SemanticIntentDomain.Chat,
                        Source = "local-semantic-router-consistent-comm",
                    };
                }

                return decision.Domain == SemanticIntentDomain.Chat
                    ? decision with { CommunicationTargets = slots }
                    : decision;
            }
            catch (JsonException) { return null; }
        }

        var match = DecisionRegex().Match(content);
        var compactMatch = CompactDecisionRegex().Match(content);
        if (!match.Success && !compactMatch.Success)
        {
            return null;
        }

        var modeCode = match.Success
            ? match.Groups["mode"].Value
            : compactMatch.Groups["mode"].Value;
        var mode = modeCode.ToUpperInvariant() switch
        {
            "A" => SemanticIntentMode.Answer,
            "O" or "W" or "V" or "B" or "F" or "P" or "S" or "R" or "X" =>
                SemanticIntentMode.Operate,
            "M" => SemanticIntentMode.Mixed,
            "C" => SemanticIntentMode.Clarify,
            _ => throw new UnreachableException(),
        };
        var domain = match.Success
            ? match.Groups["domain"].Value.ToLowerInvariant() switch
            {
                "chat" => SemanticIntentDomain.Chat,
                "app" => SemanticIntentDomain.Application,
                "file" => SemanticIntentDomain.File,
                "browser" => SemanticIntentDomain.Browser,
                "office" => SemanticIntentDomain.Office,
                "media" => SemanticIntentDomain.Media,
                "system" => SemanticIntentDomain.System,
                "research" => SemanticIntentDomain.Research,
                _ => SemanticIntentDomain.Other,
            }
            : modeCode.ToUpperInvariant() switch
            {
                "W" => SemanticIntentDomain.Chat,
                "V" => SemanticIntentDomain.Media,
                "B" => SemanticIntentDomain.Browser,
                "F" => SemanticIntentDomain.File,
                "P" => SemanticIntentDomain.Office,
                "S" => SemanticIntentDomain.System,
                "R" => SemanticIntentDomain.Research,
                "X" => SemanticIntentDomain.Application,
                _ => SemanticIntentDomain.Other,
            };
        var confidence = match.Success &&
                         int.TryParse(match.Groups["confidence"].Value, out var percentage)
            ? Math.Clamp(percentage / 100d, 0d, 1d)
            : 0.86;
        return new SemanticIntentDecision(
            mode,
            domain,
            confidence,
            elapsedMilliseconds,
            "local-semantic-router");
    }

    private static string BuildContext(
        IReadOnlyList<ConversationMessageSnapshot>? priorMessages)
    {
        if (priorMessages is not { Count: > 0 })
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var message in priorMessages.TakeLast(3))
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var role = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "用户"
                : "助手";
            var compact = Regex.Replace(message.Text, @"\s+", " ").Trim();
            if (compact.Length > 120)
            {
                compact = compact[..119] + "…";
            }

            builder.Append(role).Append('：').AppendLine(compact);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCacheKey(string request, string context)
    {
        // Punctuation, spacing and quotation may change a request's meaning.
        // Cache exact context/request pairs, never normalized trigger strings.
        return context + "\u001f" + request;
    }

    private void RememberDecision(string cacheKey, SemanticIntentDecision? decision)
    {
        if (decision is null || !decision.IsConfident)
        {
            return;
        }

        if (_decisionCache.Count >= MaximumCachedDecisions)
        {
            // This is a latency cache, not durable memory.  A coarse clear is
            // deterministic, bounded and avoids putting user text on disk.
            _decisionCache.Clear();
        }

        _decisionCache[cacheKey] = decision;
    }

    private void WriteDiagnostic(
        SemanticIntentDecision? decision,
        bool timedOut,
        long elapsedMilliseconds = 0,
        string? failure = null,
        string? finishReason = null,
        int? contentLength = null,
        int? httpStatus = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticPath)!);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                mode = decision?.Mode.ToString(),
                domain = decision?.Domain.ToString(),
                confidence = decision?.Confidence,
                elapsedMilliseconds = decision?.ElapsedMilliseconds ?? elapsedMilliseconds,
                timedOut,
                failure,
                finishReason,
                contentLength,
                httpStatus,
            });
            File.AppendAllText(_diagnosticPath, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never delay or fail a user task.
        }
    }

    public void Dispose() => _client.Dispose();

    private const string ClassificationInstructions = """
        理解完整语义、上下文、代词与省略。输出一个紧凑 JSON 对象，无解释。字段 d 取唯一大写字母：A本对话文字（答疑、评价、草稿）；W外部聊天、发送或寻找新的可交流对象；V影视音乐观看收听；B网页操作；F文件处理；P制作编辑Office成品；S系统设置；R实时查资料；X其他电脑操作；M跨类型多步；C关键目标真缺失。非 W 只输出如 {"d":"A"}。
        可打印、可编辑或要发送的成品选操作，代人聊天选 W；在社交网站寻找对象并聊天属于 W。只问方法、评价答案或要草稿，即使提到软件也选 A。不按单词判断。
        W 时额外输出 comm，内容是沟通语义槽位：{"a":"discover|chat|send|goal|file","p":"wechat|qq|soul|douyin|xiaohongshu|other|unknown","r":"单个明确对象原名|@current|unknown","m":0}。discover=找新的可聊对象；chat=按对方真实回复持续交流；send=逐字发送用户指定原文；goal=按目的联系一次；file=发送现有文件。多个平台加 ps 数组；other 加 site 网站名；多个明确对象加 rs 数组、r 为首人。多人时对每位目标能确认完整目的和专属约束才增加 tg 数组，每人一项 {"r":"该人原名","g":"仅对该人的完整目的和约束","i":"仅对该人的身份"}；若用户明确共同目的，可分别填同一目标，否则不可把一人的目的用给另一人；无法确认时省略 tg。发现新对象但未选定具体人时 r="unknown",n=true，不虚构候选人；此时增加 o 表示最终目标：shortlist=筛出候选名单，contact=建立初次联系，conversation=收到真实回复并持续对话，relationship=双方确认的恋爱关系，无法判断则 unknown。不能把后一个目标降格成前一个。排除对象加 ex 数组、筛选条件加 c 字段。仅 send 加 v 字段，逐字保留要发的完整文字；原文过长无法完整输出时省略 v，由后续提取器处理。只有共同身份明确才加 i。时长未指定则 m=0。只有当前会话明确时 r="@current"；对象无法确定填 unknown。只提取当前要求，不执行历史任务。
        """;

    [GeneratedRegex(
        @"(?:模式\s*[:：]\s*)?(?<mode>[AOMC])\s*[|｜]\s*(?:领域\s*[:：]\s*)?(?<domain>chat|app|file|browser|office|media|system|research|other)\s*(?:[|｜]\s*(?:百分数\s*[:：]\s*)?(?<confidence>\d{1,3}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecisionRegex();

    [GeneratedRegex(@"^\s*(?:模式\s*[:：]\s*)?(?<mode>[AOWVBFPSRXMC])\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompactDecisionRegex();
}
