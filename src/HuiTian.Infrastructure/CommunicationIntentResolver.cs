using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed record CommunicationIntentResolution(
    CommunicationTargetSlots? Targets,
    ChatFastPathIntent? FastPathIntent);

/// <summary>
/// Semantic slot extraction. Called only after the semantic router chose an
/// external communication task. No natural-language trigger rules here.
/// </summary>
public sealed class CommunicationIntentResolver : IDisposable
{
    private readonly HttpClient _client;
    private readonly AgentBackendConfiguration _configuration;
    private readonly string _diagnosticPath;

    public CommunicationIntentResolver(AgentBackendConfiguration configuration,
        HttpMessageHandler? handler = null, string? diagnosticsDirectory = null)
    {
        _configuration = configuration;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _diagnosticPath = Path.Combine(diagnosticsDirectory ?? new HuiTianPaths().AppState, "communication-intent.jsonl");
    }

    public async Task<ChatFastPathIntent?> ResolveAsync(
        string request,
        IReadOnlyList<ConversationMessageSnapshot> context,
        bool hasFiles,
        CancellationToken cancellationToken = default) =>
        (await ResolveDetailedAsync(request, context, hasFiles, cancellationToken)
            .ConfigureAwait(false)).FastPathIntent;

    /// <summary>
    /// Reuses communication slots from the turn classifier. Returns false if
    /// that call did not supply valid slots, so the existing semantic slot
    /// resolver can recover. No additional model request occurs here.
    /// </summary>
    public static bool TryResolveFromSemantic(
        SemanticIntentDecision? decision,
        string originalRequest,
        out CommunicationIntentResolution resolution)
    {
        resolution = new(null, null);
        if (decision is not { IsConfident: true, Mode: SemanticIntentMode.Operate,
                Domain: SemanticIntentDomain.Chat, CommunicationTargets: { } slots })
            return false;
        // A dictated message must survive byte-for-byte. If the first,
        // compact classifier omitted it, ask the dedicated extractor rather
        // than falling through as an incomplete send plan.
        if (slots.Action == "send" && string.IsNullOrWhiteSpace(slots.LiteralMessage))
            return false;
        if (!slots.FindNewPeople &&
            (slots.Platforms.Count == 0 ||
             (!slots.UseCurrentConversation && slots.TargetNames.Count == 0)))
            return false;
        // A compact first response can identify several people yet omit their
        // separate goals. Give the dedicated extractor one bounded chance to
        // recover those slots; never route a compound goal as a broadcast.
        if (slots.Action == "chat" && slots.TargetNames.Count > 1 &&
            !slots.HasDistinctInstructionsForEveryTarget &&
            !slots.TargetNames.Any(name => slots.ExcludedNames.Contains(
                name, StringComparer.OrdinalIgnoreCase)))
            return false;
        var intent = TryBuildFastPathIntent(slots, originalRequest, out var parsed)
            ? parsed : null;
        resolution = new(slots, intent);
        return true;
    }

    /// <summary>
    /// Exposes unsupported platforms and multiple named targets for a later
    /// discovery/selection workflow without converting them into send actions.
    /// </summary>
    public async Task<CommunicationIntentResolution> ResolveDetailedAsync(
        string request,
        IReadOnlyList<ConversationMessageSnapshot> context,
        bool hasFiles,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(35));
        var watch = Stopwatch.StartNew();
        var outcome = "invalid_response";
        string? finishReason = null;
        var history = string.Join("\n", context.TakeLast(4).Select(item =>
            item.Role + ":" + (item.Text.Length <= 240 ? item.Text : item.Text[..240])));
        try
        {
            using var response = await _client.PostAsJsonAsync(
                (_configuration.IsProductDefault ? "http://127.0.0.1:18180/v1" :
                    _configuration.ResponsesBaseUrl.TrimEnd('/')) + "/chat/completions",
                new
                {
                    model = _configuration.ModelName,
                    messages = new[]
                    {
                        new { role = "system", content = Instructions },
                        new { role = "user", content = $"上下文（仅用于消解指代）：\n{history}\n附有文件：{hasFiles}\n本次要求：{request}" },
                    },
                    temperature = 0,
                    // Goals are kept locally, not generated a second time. Long
                    // dictated messages still have room; the cap is not a target.
                    max_tokens = (int)Math.Clamp((long)request.Length * 2 + 128, 192, 1600),
                    response_format = new { type = "json_object" },
                    chat_template_kwargs = new { enable_thinking = false },
                    cache_prompt = true,
                }, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false));
            var choice = document.RootElement.GetProperty("choices")[0];
            finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
                ? finish.GetString() switch { "stop" => "stop", "length" => "length", "content_filter" => "content_filter", _ => "other" }
                : null;
            if (finishReason == "length") { outcome = "truncated"; return new(null, null); }
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (!TryParseTargetSlots(content, out var targets)) return new(null, null);
            if (!TryParse(content, out var intent, request))
            {
                outcome = "target_slots_only";
                return new(targets, null);
            }
            outcome = "resolved";
            return new(targets, intent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { outcome = "cancelled"; throw; }
        catch (OperationCanceledException) { outcome = "timeout"; return new(null, null); }
        catch (HttpRequestException) { outcome = "http_error"; return new(null, null); }
        catch { return new(null, null); }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticPath)!);
                File.AppendAllText(_diagnosticPath, JsonSerializer.Serialize(new {
                    at = DateTimeOffset.Now, phase = "communication_slots", elapsedMilliseconds = watch.ElapsedMilliseconds,
                    outcome, finishReason
                }) + Environment.NewLine);
            }
            catch { /* Diagnostics must not fail the task; no user/contact text is stored. */ }
        }
    }

    public static bool TryParse(string? content, out ChatFastPathIntent intent, string? originalRequest = null)
    {
        intent = null!;
        try
        {
            using var doc = JsonDocument.Parse(content ?? "");
            var root = doc.RootElement;
            return TryParseTargetSlots(root, out var targets) &&
                TryBuildFastPathIntent(targets, originalRequest, out intent);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static bool TryBuildFastPathIntent(
        CommunicationTargetSlots targets,
        string? originalRequest,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        try
        {
            if (targets.FindNewPeople || targets.Platforms.Count != 1 ||
                (targets.TargetNames.Count != 1 && !targets.UseCurrentConversation) ||
                targets.ExcludedNames.Any(name => targets.TargetNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
                return false;
            var platform = targets.Platforms[0] switch
            {
                "微信" => ChatFastPathPlatform.WeChat,
                "QQ" => ChatFastPathPlatform.QQ,
                _ => (ChatFastPathPlatform?)null,
            };
            var action = targets.Action;
            var recipient = targets.UseCurrentConversation
                ? "@current" : targets.TargetNames.SingleOrDefault();
            // Literal message whitespace is content. Never normalize it with IDs.
            var value = targets.LiteralMessage;
            var persona = targets.SharedPersona;
            var goal = action is "chat" or "goal"
                ? (string.IsNullOrWhiteSpace(originalRequest) ? value : originalRequest) : value;
            if (platform is null || action is not ("chat" or "send" or "goal" or "file") ||
                string.IsNullOrWhiteSpace(recipient) || recipient.Equals("unknown", StringComparison.OrdinalIgnoreCase) || recipient.Length > 80 ||
                (action != "file" && string.IsNullOrWhiteSpace(goal))) return false;

            var current = recipient == "@current";
            var platformName = platform == ChatFastPathPlatform.WeChat ? "微信" : "QQ";
            var plan = action switch
            {
                "send" => CommunicationPlanCompiler.CompileLiteral(platformName, recipient, value!),
                "chat" or "goal" => CommunicationPlanCompiler.CompileGoal(platformName, recipient, goal!, persona,
                    action == "chat", targets.RequestedDuration, current),
                _ => null,
            };
            // Semantic goals must not go through the legacy directive/tone
            // rewriter or be treated as a literal message. Keep the full request
            // as private context and require natural composition before sending.
            if (plan is not null && action is "chat" or "goal") plan = plan with
            {
                OriginalGoal = goal!,
                Objectives = [new CommunicationObjective(1, CommunicationObjectiveKind.Other, goal!, goal!)],
                OpeningMessage = string.Empty,
            };
            if (plan is not null) plan = plan with { UseCurrentConversation = current };
            intent = new ChatFastPathIntent(platform.Value, current ? null : recipient,
                action == "send" ? value : plan?.OpeningMessage,
                current, action == "file", false, value ?? goal ?? "")
            {
                MessageComposedFromGoal = action is "chat" or "goal",
                CommunicationGoal = goal,
                Persona = persona,
                CommunicationPlan = plan,
            };
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    public static bool TryParseTargetSlots(string? content, out CommunicationTargetSlots slots)
    {
        slots = null!;
        try
        {
            using var doc = JsonDocument.Parse(content ?? "");
            return TryParseTargetSlots(doc.RootElement, out slots);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseTargetSlots(JsonElement root, out CommunicationTargetSlots slots)
    {
        slots = null!;
        if (root.ValueKind != JsonValueKind.Object) return false;
        string? Read(string key) => root.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        var action = Read("a");
        if (action is not ("discover" or "chat" or "send" or "goal" or "file")) return false;
        if (!TryReadList(root, "ps", 8, out var rawPlatforms) ||
            !TryReadList(root, "rs", 12, out var targetNames) ||
            !TryReadList(root, "ex", 20, out var excludedNames)) return false;

        var primaryPlatform = Read("p");
        if (!string.IsNullOrWhiteSpace(primaryPlatform) &&
            !primaryPlatform.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
            !rawPlatforms.Contains(primaryPlatform, StringComparer.OrdinalIgnoreCase))
            rawPlatforms.Insert(0, primaryPlatform);
        var site = Read("site");
        var platforms = new List<string>();
        foreach (var raw in rawPlatforms)
        {
            var platform = CanonicalizeTargetPlatform(raw, site);
            if (platform is null) return false;
            if (!platforms.Contains(platform, StringComparer.OrdinalIgnoreCase)) platforms.Add(platform);
        }

        var singleTarget = Read("r");
        if (singleTarget is { Length: > 80 }) return false;
        var current = string.Equals(singleTarget, "@current", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(singleTarget) &&
            !singleTarget.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !current)
        {
            if (targetNames.Count > 0 &&
                !targetNames.Contains(singleTarget, StringComparer.OrdinalIgnoreCase)) return false;
            if (targetNames.Count == 0) targetNames.Add(singleTarget);
        }
        if (targetNames.RemoveAll(name => name == "@current") > 0) current = true;
        if (current && targetNames.Count > 0) return false;

        var findNewPeople = action == "discover" ||
            (root.TryGetProperty("n", out var newPeople) && newPeople.ValueKind == JsonValueKind.True);
        var desiredOutcome = SocialDiscoveryDesiredOutcome.Unspecified;
        if (root.TryGetProperty("o", out var outcomeNode))
        {
            if (outcomeNode.ValueKind != JsonValueKind.String) return false;
            var outcomeValue = outcomeNode.GetString()?.Trim().ToLowerInvariant();
            if (outcomeValue is not ("shortlist" or "contact" or "conversation" or
                "relationship" or "unknown")) return false;
            desiredOutcome = outcomeValue switch
            {
                "shortlist" => SocialDiscoveryDesiredOutcome.CandidateShortlist,
                "contact" => SocialDiscoveryDesiredOutcome.ContactInitiated,
                "conversation" => SocialDiscoveryDesiredOutcome.ReciprocalConversation,
                "relationship" => SocialDiscoveryDesiredOutcome.RelationshipEstablished,
                _ => SocialDiscoveryDesiredOutcome.Unspecified,
            };
        }
        var criteria = Read("c");
        if (criteria is { Length: > 500 }) return false;
        var requestedDuration = root.TryGetProperty("m", out var duration) &&
            duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var minutes) &&
            double.IsFinite(minutes) && minutes > 0 && minutes < TimeSpan.MaxValue.TotalMinutes
                ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;

        var targetInstructions = new List<CommunicationTargetInstruction>();
        if (root.TryGetProperty("tg", out var perTarget))
        {
            if (perTarget.ValueKind != JsonValueKind.Array || perTarget.GetArrayLength() > 12)
                return false;
            foreach (var item in perTarget.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                string? ReadItem(string key) => item.TryGetProperty(key, out var field) &&
                    field.ValueKind == JsonValueKind.String ? field.GetString()?.Trim() : null;
                var name = ReadItem("r");
                var targetGoal = ReadItem("g");
                var persona = ReadItem("i");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
                    string.IsNullOrWhiteSpace(targetGoal) || targetGoal.Length > 500 ||
                    persona is { Length: > 80 } ||
                    !targetNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    targetInstructions.Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return false;
                targetInstructions.Add(new CommunicationTargetInstruction(name, targetGoal, persona));
            }
        }
        var literalMessage = root.TryGetProperty("v", out var payload) &&
            payload.ValueKind == JsonValueKind.String ? payload.GetString() : null;
        if (literalMessage is { Length: > 4000 }) return false;
        var sharedPersona = Read("i");
        if (sharedPersona is { Length: > 80 }) return false;
        slots = new CommunicationTargetSlots(action, platforms.ToArray(),
            targetNames.ToArray(), excludedNames.ToArray(), findNewPeople,
            current, criteria, requestedDuration)
        {
            LiteralMessage = literalMessage,
            SharedPersona = sharedPersona,
            TargetInstructions = targetInstructions.ToArray(),
            DesiredOutcome = desiredOutcome,
        };
        return true;
    }

    private static bool TryReadList(JsonElement root, string key, int maximum,
        out List<string> values)
    {
        values = [];
        if (!root.TryGetProperty(key, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum) return false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            var value = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
                value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
            values.Add(value);
        }
        return true;
    }

    private static string? CanonicalizeTargetPlatform(string value, string? site) =>
        CommunicationPlatformIdentity.Canonicalize(value) ?? value.ToLowerInvariant() switch
        {
            "soul" => "Soul",
            "抖音" or "douyin" => "抖音",
            "小红书" or "xiaohongshu" => "小红书",
            "other" when !string.IsNullOrWhiteSpace(site) && site.Length <= 80 => site,
            _ => null,
        };

    public void Dispose() => _client.Dispose();

    private const string Instructions = """
        你只提取外部沟通意图。理解完整要求和上下文、代词、省略，不要求固定句式。只输出紧凑JSON：
        {"a":"discover|chat|send|goal|file|unknown","p":"wechat|qq|soul|douyin|xiaohongshu|other|unknown","r":"单个明确对象原名|@current|unknown","m":分钟数}
        discover=寻找新的可聊天对象，chat=持续交流，send=用户指定原文发送一次，goal=按目的拟写后联系一次，file=发送现有文件。涉及多个平台时增加ps数组（平台标识）；p取第一个平台。other时增加site为网站名称。涉及多个明确对象时增加rs数组，每项是用户提到的原名；r取其中第一个。多人时对每位目标能确认目的才增加tg数组，每项{"r":"该联系人原名","g":"仅针对该人的目的","i":"仅针对该人的身份或语气"}；明确共同目的时可分别填同一目的，不得把甲的目的套给乙；无法确认就不输出tg。找新对象而尚无具体人时r=unknown、n=true；绝不根据描述虚构候选人或账号。n=true时增加o字段表示用户最终希望达成的层级：shortlist=候选名单，contact=向合适对象建立初次联系，conversation=收到真实回复并持续对话，relationship=建立双方确认的亲密关系；含糊则unknown。不要把关系目标降成名单目标。用户要求避开的人或关系增加ex数组，例如["家人"]；择人条件可放c字段，不要把条件当作联系人。仅send增加v字段，逐字保留要发送的完整内容。chat/goal的完整目的由执行器直接保留本次原文，不要概括成消息。仅有明确共同身份时增加i字段。没有指定时长m=0。明确当前会话才r=@current；从上下文能确定平台/对象才补全，不能确定填unknown。只提取这次要求，不执行上下文中的旧任务。不要回答、不要推理文字。
        """;
}
