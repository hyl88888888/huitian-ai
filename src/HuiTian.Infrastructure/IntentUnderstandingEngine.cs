using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

/// <summary>
/// Compiles free-form language and explicitly separated runtime contexts into
/// a data-only semantic contract.  It never searches user text for commands or
/// application keywords; deterministic routing is used only if the local
/// semantic service is unavailable.
/// </summary>
public sealed class IntentUnderstandingEngine : IDisposable
{
    private const int MaximumCachedResolutions = 128;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _diagnosticPath;
    private readonly TimeSpan _resolutionTimeout;
    private readonly ConcurrentDictionary<string, IntentResolution> _cache =
        new(StringComparer.Ordinal);

    public IntentUnderstandingEngine(
        AgentBackendConfiguration configuration,
        HuiTianPaths paths,
        Uri? semanticBaseUri = null,
        TimeSpan? resolutionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        _model = configuration.ModelName;
        var baseUrl = semanticBaseUri?.AbsoluteUri ?? configuration.ResponsesBaseUrl;
        _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        _diagnosticPath = Path.Combine(paths.AppState, "intent-understanding.jsonl");
        _resolutionTimeout = resolutionTimeout ?? ResolveDefaultTimeout(_endpoint);
        _client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<IntentResolution> ResolveAsync(
        string currentRequest,
        IntentContextSnapshot context,
        AgentTaskRoute fallbackRoute,
        bool hasAttachments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentRequest))
        {
            throw new ArgumentException("A current request is required.", nameof(currentRequest));
        }

        ArgumentNullException.ThrowIfNull(context);
        var input = BuildInput(currentRequest, context, hasAttachments);
        var cacheKey = BuildCacheKey(input);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            var cacheHit = CopyTiming(cached, 0, "semantic-cache");
            WriteDiagnostic(cacheHit, "cache", timedOut: false);
            return cacheHit;
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_resolutionTimeout);
        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = SemanticCompilerInstructions },
                    new { role = "user", content = input },
                },
                temperature = 0,
                max_tokens = 160,
                stream = false,
                response_format = new { type = "json_object" },
                chat_template_kwargs = new { enable_thinking = false },
                cache_prompt = true,
            };

            using var response = await _client.PostAsJsonAsync(
                    _endpoint,
                    body,
                    timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            stopwatch.Stop();
            if (!TryParseResolution(
                    content,
                    stopwatch.ElapsedMilliseconds,
                    "local-semantic-engine",
                    out var resolution))
            {
                var fallback = BuildFallback(
                    currentRequest,
                    fallbackRoute,
                    hasAttachments,
                    stopwatch.ElapsedMilliseconds,
                    "invalid-json-fallback");
                WriteDiagnostic(fallback, "invalid-json", timedOut: false);
                return fallback;
            }

            Remember(cacheKey, resolution);
            WriteDiagnostic(resolution, "model", timedOut: false);
            return resolution;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var fallback = BuildFallback(
                currentRequest,
                fallbackRoute,
                hasAttachments,
                stopwatch.ElapsedMilliseconds,
                "timeout-inconclusive");
            WriteDiagnostic(fallback, "timeout", timedOut: true);
            return fallback;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var fallback = BuildFallback(
                currentRequest,
                fallbackRoute,
                hasAttachments,
                stopwatch.ElapsedMilliseconds,
                "unavailable-fallback");
            WriteDiagnostic(fallback, exception.GetType().Name, timedOut: false);
            return fallback;
        }
    }

    private static TimeSpan ResolveDefaultTimeout(Uri endpoint)
    {
        if (double.TryParse(
                Environment.GetEnvironmentVariable("HUITIAN_INTENT_TIMEOUT_SECONDS"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var configured))
        {
            return TimeSpan.FromSeconds(Math.Clamp(configured, 5, 180));
        }

        // The main 27B endpoint has a substantially larger first-token budget
        // than the small optional router.  A fixed 14-second deadline made the
        // entire evaluator report semantic failures without receiving a
        // single semantic answer.
        return endpoint.Port == 18180
            ? TimeSpan.FromSeconds(90)
            : TimeSpan.FromSeconds(35);
    }

    public static bool TryParseResolution(
        string? content,
        long elapsedMilliseconds,
        string source,
        out IntentResolution resolution)
    {
        resolution = null!;
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var taskType = ParseTaskType(GetString(root, "task_type", "type", "t"));
            var domain = ParseDomain(GetString(root, "domain", "d"));
            var goal = GetString(root, "goal", "g")?.Trim();
            if (taskType is null || domain is null || string.IsNullOrWhiteSpace(goal))
            {
                return false;
            }

            var requiresComputer = GetBoolean(root, "requires_computer", "c") ??
                                   taskType is SemanticTaskType.ComputerAction or
                                       SemanticTaskType.ComputerTask or
                                       SemanticTaskType.Hybrid or
                                       SemanticTaskType.MultiAppTask;
            var risk = ParseRisk(GetString(root, "risk", "r")) ?? IntentRiskLevel.None;
            var actions = ReadStringArray(root, "actions", "a");
            var applications = ReadStringArray(root, "applications", "apps", "p");
            var entities = ReadEntities(root);
            var constraints = ReadStringArray(root, "constraints", "n");
            var deliverables = ReadDeliverables(root, goal);
            if (deliverables.Count == 0)
            {
                deliverables = BuildImplicitDeliverables(
                    taskType.Value,
                    domain.Value,
                    requiresComputer,
                    goal);
            }

            var conditions = ReadConditions(root, "success_conditions", "s");
            if (conditions.Count == 0)
            {
                conditions = BuildImplicitConditions(deliverables, requiresComputer, goal);
            }

            var plan = ReadPlan(root);
            if (plan.Count == 0 && requiresComputer)
            {
                plan = BuildImplicitPlan(
                    actions,
                    applications,
                    deliverables,
                    goal,
                    risk);
            }
            var clarification = root.TryGetProperty("clarification", out var clarificationNode) &&
                                clarificationNode.ValueKind == JsonValueKind.Object
                ? clarificationNode
                : default;
            var requiresClarification = GetBoolean(root, "requires_clarification", "l") ??
                                        GetBoolean(clarification, "required", "l") ??
                                        taskType == SemanticTaskType.Clarify;
            var reason = GetString(root, "clarification_reason") ??
                         GetString(clarification, "reason");
            var question = GetString(root, "clarification_question") ??
                           GetString(clarification, "question");
            var confidence = GetDouble(root, "confidence", "cf") ?? 0.75;

            resolution = new IntentResolution(
                taskType.Value,
                domain.Value,
                goal,
                requiresComputer,
                actions,
                applications,
                entities,
                constraints,
                conditions,
                plan,
                risk,
                requiresClarification,
                reason,
                question,
                confidence,
                source,
                elapsedMilliseconds,
                deliverables);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static IntentResolution BuildFallback(
        string currentRequest,
        AgentTaskRoute route,
        bool hasAttachments,
        long elapsedMilliseconds = 0,
        string source = "deterministic-fallback")
    {
        var effectiveRoute = hasAttachments && route == AgentTaskRoute.DirectAnswer
            ? AgentTaskRoute.FileWork
            : route;
        var taskType = effectiveRoute switch
        {
            AgentTaskRoute.DirectAnswer => SemanticTaskType.Answer,
            AgentTaskRoute.Composite => SemanticTaskType.MultiAppTask,
            AgentTaskRoute.FileWork or AgentTaskRoute.Presentation => SemanticTaskType.ComputerTask,
            _ => SemanticTaskType.ComputerAction,
        };
        var domain = effectiveRoute switch
        {
            AgentTaskRoute.DirectAnswer => IntentExecutionDomain.Answer,
            AgentTaskRoute.Research => IntentExecutionDomain.Research,
            AgentTaskRoute.FileWork => IntentExecutionDomain.File,
            AgentTaskRoute.Presentation => IntentExecutionDomain.Office,
            AgentTaskRoute.Composite => IntentExecutionDomain.MultiApp,
            _ => IntentExecutionDomain.Other,
        };
        var evidence = effectiveRoute switch
        {
            AgentTaskRoute.DirectAnswer or AgentTaskRoute.Research =>
                new IntentSuccessCondition(IntentEvidenceKind.ReturnValue, "The requested result is available."),
            AgentTaskRoute.FileWork or AgentTaskRoute.Presentation =>
                new IntentSuccessCondition(IntentEvidenceKind.Artifact, "The requested artifact exists and can be opened."),
            AgentTaskRoute.Composite =>
                new IntentSuccessCondition(IntentEvidenceKind.StateObservation, "Every requested stage has observable completion evidence."),
            _ => new IntentSuccessCondition(IntentEvidenceKind.StateObservation, "The requested application state is observed."),
        };
        return new IntentResolution(
            taskType,
            domain,
            currentRequest.Trim(),
            effectiveRoute != AgentTaskRoute.DirectAnswer &&
            effectiveRoute != AgentTaskRoute.Research,
            actions: [effectiveRoute.ToString()],
            successConditions: [evidence],
            risk: effectiveRoute == AgentTaskRoute.DirectAnswer
                ? IntentRiskLevel.None
                : IntentRiskLevel.Reversible,
            confidence: 0.35,
            source: source,
            elapsedMilliseconds: elapsedMilliseconds);
    }

    private static string BuildInput(
        string request,
        IntentContextSnapshot context,
        bool hasAttachments)
    {
        var payload = new
        {
            q = request.Trim(),
            f = hasAttachments,
            x = new
            {
                c = context.Conversation.TakeLast(6).Select(message => new
                {
                    r = message.Role,
                    t = Compact(message.Text, 240),
                    f = message.Attachments?.Select(Path.GetFileName).Take(4),
                }),
                d = context.Desktop,
                t = context.Task,
                a = context.Application,
                p = context.UserPreferences.TakeLast(6),
                e = context.RecentEntities,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string BuildCacheKey(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private static IntentResolution CopyTiming(
        IntentResolution source,
        long elapsedMilliseconds,
        string resolutionSource) => new(
        source.TaskType,
        source.Domain,
        source.Goal,
        source.RequiresComputer,
        source.Actions,
        source.Applications,
        source.Entities,
        source.Constraints,
        source.SuccessConditions,
        source.Plan,
        source.Risk,
        source.RequiresClarification,
        source.ClarificationReason,
        source.ClarificationQuestion,
        source.Confidence,
        resolutionSource,
        elapsedMilliseconds,
        source.Deliverables);

    private void Remember(string key, IntentResolution resolution)
    {
        if (!resolution.IsConfident) return;
        if (_cache.Count >= MaximumCachedResolutions) _cache.Clear();
        _cache[key] = resolution;
    }

    private void WriteDiagnostic(IntentResolution resolution, string outcome, bool timedOut)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticPath)!);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                taskType = resolution.TaskType.ToString(),
                domain = resolution.Domain.ToString(),
                requiresComputer = resolution.RequiresComputer,
                applications = resolution.Applications.Count,
                actions = resolution.Actions.Count,
                planSteps = resolution.Plan.Count,
                confidence = resolution.Confidence,
                elapsedMilliseconds = resolution.ElapsedMilliseconds,
                source = resolution.Source,
                outcome,
                timedOut,
            });
            File.AppendAllText(_diagnosticPath, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics are best-effort and never block the task.
        }
    }

    private static string? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var value = content.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                value = value[(firstLine + 1)..lastFence].Trim();
            }
        }

        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value[start..(end + 1)] : null;
    }

    private static SemanticTaskType? ParseTaskType(string? value) => NormalizeToken(value) switch
    {
        "answer" or "a" => SemanticTaskType.Answer,
        "create" or "c" => SemanticTaskType.Create,
        "computer_action" or "ca" => SemanticTaskType.ComputerAction,
        "computer_task" or "ct" => SemanticTaskType.ComputerTask,
        "hybrid" or "h" => SemanticTaskType.Hybrid,
        "multi_app_task" or "m" => SemanticTaskType.MultiAppTask,
        "clarify" or "q" => SemanticTaskType.Clarify,
        _ => null,
    };

    private static IntentExecutionDomain? ParseDomain(string? value) => NormalizeToken(value) switch
    {
        "answer" or "a" => IntentExecutionDomain.Answer,
        "research" or "r" => IntentExecutionDomain.Research,
        "file" or "f" => IntentExecutionDomain.File,
        "office" or "o" => IntentExecutionDomain.Office,
        "browser" or "b" => IntentExecutionDomain.Browser,
        "communication" or "c" => IntentExecutionDomain.Communication,
        "media" or "m" => IntentExecutionDomain.Media,
        "system" or "s" => IntentExecutionDomain.System,
        "development" or "d" => IntentExecutionDomain.Development,
        "other" or "x" => IntentExecutionDomain.Other,
        "multi_app" or "u" => IntentExecutionDomain.MultiApp,
        _ => null,
    };

    private static IntentRiskLevel? ParseRisk(string? value) => NormalizeToken(value) switch
    {
        "none" or "n" => IntentRiskLevel.None,
        "reversible" or "r" => IntentRiskLevel.Reversible,
        "external_effect" or "e" => IntentRiskLevel.ExternalEffect,
        "high_impact" or "h" => IntentRiskLevel.HighImpact,
        _ => null,
    };

    private static IntentEntityRole ParseRole(string? value) => NormalizeToken(value) switch
    {
        "object" => IntentEntityRole.Object,
        "target" => IntentEntityRole.Target,
        "recipient" => IntentEntityRole.Recipient,
        "source" => IntentEntityRole.Source,
        "location" => IntentEntityRole.Location,
        "application" => IntentEntityRole.Application,
        "person" => IntentEntityRole.Person,
        "file" => IntentEntityRole.File,
        "time" => IntentEntityRole.Time,
        "quantity" => IntentEntityRole.Quantity,
        "context_reference" => IntentEntityRole.ContextReference,
        _ => IntentEntityRole.Other,
    };

    private static IntentEvidenceKind ParseEvidenceKind(string? value) => NormalizeToken(value) switch
    {
        "return_value" or "rv" => IntentEvidenceKind.ReturnValue,
        "artifact" or "art" => IntentEvidenceKind.Artifact,
        "external_state" or "ext" => IntentEvidenceKind.ExternalState,
        "user_confirmation" or "usr" => IntentEvidenceKind.UserConfirmation,
        _ => IntentEvidenceKind.StateObservation,
    };

    private static IntentDeliverableKind ParseDeliverableKind(string? value) =>
        NormalizeToken(value) switch
        {
            "conversational_answer" or "answer" => IntentDeliverableKind.ConversationalAnswer,
            "research_result" or "research" => IntentDeliverableKind.ResearchResult,
            "document" or "doc" => IntentDeliverableKind.Document,
            "spreadsheet" or "sheet" => IntentDeliverableKind.Spreadsheet,
            "presentation" or "slides" => IntentDeliverableKind.Presentation,
            "image" => IntentDeliverableKind.Image,
            "audio" => IntentDeliverableKind.Audio,
            "video" => IntentDeliverableKind.Video,
            "modified_file" or "modified" => IntentDeliverableKind.ModifiedFile,
            "application_state" or "state" => IntentDeliverableKind.ApplicationState,
            "external_delivery" or "send" => IntentDeliverableKind.ExternalDelivery,
            _ => IntentDeliverableKind.File,
        };

    private static IReadOnlyList<IntentEntity> ReadEntities(JsonElement root)
    {
        if ((!root.TryGetProperty("entities", out var node) && !root.TryGetProperty("e", out node)) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IntentEntity>();
        }

        var result = new List<IntentEntity>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(item, "type", "t");
            var value = GetString(item, "value", "v");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value)) continue;
            result.Add(new IntentEntity(
                ParseRole(GetString(item, "role", "r")),
                type,
                value,
                GetString(item, "resolved_value", "z"),
                GetDouble(item, "confidence", "c") ?? 1d));
        }

        return result;
    }

    private static IReadOnlyList<IntentSuccessCondition> ReadConditions(
        JsonElement root,
        params string[] properties)
    {
        var node = default(JsonElement);
        if (!properties.Any(property => root.TryGetProperty(property, out node)) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IntentSuccessCondition>();
        }

        var result = new List<IntentSuccessCondition>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(new IntentSuccessCondition(
                    IntentEvidenceKind.StateObservation,
                    item.GetString()!));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            var description = GetString(item, "description", "x");
            if (string.IsNullOrWhiteSpace(description)) continue;
            result.Add(new IntentSuccessCondition(
                ParseEvidenceKind(GetString(item, "kind", "k")),
                description));
        }

        return result;
    }

    private static IReadOnlyList<IntentDeliverableRequirement> ReadDeliverables(
        JsonElement root,
        string goal)
    {
        if ((!root.TryGetProperty("deliverables", out var node) && !root.TryGetProperty("v", out node)) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IntentDeliverableRequirement>();
        }

        var result = new List<IntentDeliverableRequirement>();
        var rootDestination = GetString(root, "destination", "z");
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
            {
                var kind = ParseDeliverableKind(item.GetString());
                result.Add(new IntentDeliverableRequirement(
                    kind,
                    goal,
                    DefaultDeliverableFormat(kind),
                    kind == IntentDeliverableKind.ExternalDelivery
                        ? rootDestination
                        : null));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            var description = GetString(item, "description", "x");
            if (string.IsNullOrWhiteSpace(description)) continue;
            result.Add(new IntentDeliverableRequirement(
                ParseDeliverableKind(GetString(item, "kind", "k")),
                description,
                GetString(item, "format", "f"),
                GetString(item, "destination", "z"),
                GetString(item, "artifactKey", "id")));
        }

        return result;
    }

    private static IReadOnlyList<IntentDeliverableRequirement> BuildImplicitDeliverables(
        SemanticTaskType taskType,
        IntentExecutionDomain domain,
        bool requiresComputer,
        string goal)
    {
        var kind = taskType switch
        {
            SemanticTaskType.Answer => IntentDeliverableKind.ConversationalAnswer,
            _ when domain == IntentExecutionDomain.Research && !requiresComputer =>
                IntentDeliverableKind.ResearchResult,
            SemanticTaskType.Create when !requiresComputer =>
                IntentDeliverableKind.ConversationalAnswer,
            SemanticTaskType.Create => IntentDeliverableKind.File,
            SemanticTaskType.ComputerAction => IntentDeliverableKind.ApplicationState,
            SemanticTaskType.ComputerTask or SemanticTaskType.Hybrid or SemanticTaskType.MultiAppTask =>
                IntentDeliverableKind.ApplicationState,
            _ => IntentDeliverableKind.ConversationalAnswer,
        };
        return [new IntentDeliverableRequirement(kind, goal, DefaultDeliverableFormat(kind))];
    }

    private static IReadOnlyList<IntentSuccessCondition> BuildImplicitConditions(
        IReadOnlyList<IntentDeliverableRequirement> deliverables,
        bool requiresComputer,
        string goal)
    {
        var results = deliverables
            .Select(deliverable => new IntentSuccessCondition(
                EvidenceFor(deliverable.Kind),
                deliverable.Description))
            .GroupBy(condition => condition.Kind)
            .Select(group => group.First())
            .ToList();
        if (results.Count == 0)
        {
            results.Add(new IntentSuccessCondition(
                requiresComputer ? IntentEvidenceKind.StateObservation : IntentEvidenceKind.ReturnValue,
                goal));
        }

        return results;
    }

    private static IReadOnlyList<IntentPlanStep> BuildImplicitPlan(
        IReadOnlyList<string> semanticActions,
        IReadOnlyList<string> applications,
        IReadOnlyList<IntentDeliverableRequirement> deliverables,
        string goal,
        IntentRiskLevel risk)
    {
        var actions = semanticActions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToList();
        var hasArtifact = deliverables.Any(deliverable =>
            EvidenceFor(deliverable.Kind) == IntentEvidenceKind.Artifact);
        var hasExternalDelivery = deliverables.Any(deliverable =>
            deliverable.Kind == IntentDeliverableKind.ExternalDelivery);
        var requiredSteps = hasArtifact ? 3 : 1;
        if (hasExternalDelivery) requiredSteps++;
        var synthesized = new[] { "准备内容", "生成交付物", "验证交付物", "完成外部交付" };
        while (actions.Count < requiredSteps)
        {
            actions.Add(synthesized[actions.Count]);
        }

        var steps = new List<IntentPlanStep>();
        for (var index = 0; index < actions.Count; index++)
        {
            var isExternalStep = hasExternalDelivery && index == actions.Count - 1;
            var isArtifactStep = hasArtifact && !isExternalStep && index == actions.Count - (hasExternalDelivery ? 2 : 1);
            var evidence = isExternalStep
                ? IntentEvidenceKind.ExternalState
                : isArtifactStep
                    ? IntentEvidenceKind.Artifact
                    : IntentEvidenceKind.StateObservation;
            var id = $"step-{index + 1}";
            steps.Add(new IntentPlanStep(
                id,
                actions[index],
                actions[index],
                applications.Count == 0 ? null : applications[Math.Min(index, applications.Count - 1)],
                index == actions.Count - 1 ? goal : null,
                index == 0 ? [] : [$"step-{index}"],
                [new IntentSuccessCondition(evidence, actions[index])],
                isExternalStep ? IntentRiskLevel.ExternalEffect : risk));
        }

        return steps;
    }

    private static IntentEvidenceKind EvidenceFor(IntentDeliverableKind kind) => kind switch
    {
        IntentDeliverableKind.ConversationalAnswer or IntentDeliverableKind.ResearchResult =>
            IntentEvidenceKind.ReturnValue,
        IntentDeliverableKind.Document or IntentDeliverableKind.Spreadsheet or
            IntentDeliverableKind.Presentation or IntentDeliverableKind.Image or
            IntentDeliverableKind.Audio or IntentDeliverableKind.Video or
            IntentDeliverableKind.File or IntentDeliverableKind.ModifiedFile =>
            IntentEvidenceKind.Artifact,
        IntentDeliverableKind.ExternalDelivery => IntentEvidenceKind.ExternalState,
        _ => IntentEvidenceKind.StateObservation,
    };

    private static string? DefaultDeliverableFormat(IntentDeliverableKind kind) => kind switch
    {
        IntentDeliverableKind.Document => "docx",
        IntentDeliverableKind.Spreadsheet => "xlsx",
        IntentDeliverableKind.Presentation => "pptx",
        _ => null,
    };

    private static IReadOnlyList<IntentPlanStep> ReadPlan(JsonElement root)
    {
        if ((!root.TryGetProperty("plan", out var node) && !root.TryGetProperty("q", out node)) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IntentPlanStep>();
        }

        var result = new List<IntentPlanStep>();
        var index = 0;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            index++;
            var id = GetString(item, "id", "i") ?? $"step-{index}";
            var title = GetString(item, "title", "x");
            var action = GetString(item, "action", "a");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(action)) continue;
            result.Add(new IntentPlanStep(
                id,
                title,
                action,
                GetString(item, "app", "p"),
                GetString(item, "target", "o"),
                ReadStringArray(item, "depends_on", "u"),
                ReadConditions(item, "success_conditions", "s"),
                ParseRisk(GetString(item, "risk", "r")) ?? IntentRiskLevel.Reversible,
                GetInteger(item, "estimated_latency_ms", "m") ?? 0));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!root.TryGetProperty(property, out var node)) continue;
            if (node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString()))
            {
                return [node.GetString()!];
            }

            if (node.ValueKind == JsonValueKind.Array)
            {
                return node.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String &&
                                   !string.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!)
                    .ToArray();
            }
        }

        return Array.Empty<string>();
    }

    private static string? GetString(JsonElement root, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in properties)
        {
            if (root.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString();
            }
        }

        return null;
    }

    private static bool? GetBoolean(JsonElement root, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var node = default(JsonElement);
        if (!properties.Any(property => root.TryGetProperty(property, out node))) return null;

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement root, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in properties)
        {
            if (root.TryGetProperty(property, out var node) && node.TryGetDouble(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetInteger(JsonElement root, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in properties)
        {
            if (root.TryGetProperty(property, out var node) && node.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeToken(string? value) =>
        (value ?? string.Empty)
        .Trim()
        .Replace('-', '_')
        .Replace(' ', '_')
        .ToLowerInvariant();

    private static string Compact(string? value, int maximumLength)
    {
        var compact = string.Join(' ', (value ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximumLength
            ? compact
            : compact[..Math.Max(1, maximumLength - 1)] + "…";
    }

    public void Dispose() => _client.Dispose();

    private const string SemanticCompilerInstructions = """
        Semantic compiler for a Windows agent. Infer the user's real outcome from the whole request and separated context, never from isolated trigger words. Decide answer versus observable computer work. Resolve safe omissions/pronouns; clarify only costly true ambiguity.

        Output ONE minified JSON object, no markdown/reasoning. Use compact keys exactly:
        {"t":"a|c|ca|ct|h|m|q","d":"a|r|f|o|b|c|m|s|d|x|u","g":"resolved goal","c":true,"a":["ordered action"],"p":["logical app"],"v":["answer|research|document|spreadsheet|presentation|image|audio|video|file|modified|state|send"],"z":"recipient or null","r":"n|r|e|h","l":false,"cf":0.9}

        t: answer, create, computer_action, computer_task, hybrid, multi_app_task, clarify. d: answer,research,file,office,browser,communication,media,system,development,other,multi_app. For computer work a has 1-4 ordered actions; pure answer a=[]. v lists only final usable/openable/printable/sent results, never search or planning. Runtime derives verification and dependency steps from v+a.

        Carrier rules are semantic, not keyword triggers: a printable/editable paper, form, exam, manual, report or notice is v=document; rows/columns/calculation/data tables are v=spreadsheet; slides/deck are v=presentation; changing an existing file is v=modified. If the result must reach another person/service append v=send and put destination in z. Send/upload/publish risk=e; payment, credentials, permanent deletion, account security risk=h. Keep g and every action very short; whole JSON under 110 tokens.
        Prefer one v entry per final artifact. If two properties of the SAME artifact require separate entries, use objects {"k":"kind","x":"description","f":"extension","id":"artifact identity"} sharing id. Different files must have different ids; never merge separately requested outputs.
        """;
}
