using System.Net;
using System.Text;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class RouterContractRegression
{
    public static async Task RunAsync()
    {
        // Synthetic HTTP responses test the wire/parse contract and state
        // handling only. They do not measure a live model's semantic accuracy.
        await LocalStructuredDecisionAsync();
        await StructuredCommunicationAsync();
        await ExternalCompatibilityAsync();
        await InvalidResponsesAsync();
        await CacheAndContextAsync();
        await CancellationAsync();
        Console.WriteLine("PASS: isolated router HTTP contract, diagnostics, cache/context, and cancellation (no model inference)");
    }

    private static async Task LocalStructuredDecisionAsync()
    {
        var codes = new (string Code, SemanticIntentMode Mode, SemanticIntentDomain Domain)[]
        {
            ("A", SemanticIntentMode.Answer, SemanticIntentDomain.Other),
            ("W", SemanticIntentMode.Operate, SemanticIntentDomain.Chat),
            ("V", SemanticIntentMode.Operate, SemanticIntentDomain.Media),
            ("B", SemanticIntentMode.Operate, SemanticIntentDomain.Browser),
            ("F", SemanticIntentMode.Operate, SemanticIntentDomain.File),
            ("P", SemanticIntentMode.Operate, SemanticIntentDomain.Office),
            ("S", SemanticIntentMode.Operate, SemanticIntentDomain.System),
            ("R", SemanticIntentMode.Operate, SemanticIntentDomain.Research),
            ("X", SemanticIntentMode.Operate, SemanticIntentDomain.Application),
            ("M", SemanticIntentMode.Mixed, SemanticIntentDomain.Other),
            ("C", SemanticIntentMode.Clarify, SemanticIntentDomain.Other),
        };
        var index = 0;
        var handler = new CaptureHandler(_ => Task.FromResult(Completion(
            JsonSerializer.Serialize(new { d = codes[index++].Code }))));
        var paths = NewPaths();
        using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, paths, handler);
        foreach (var sample in codes)
        {
            var decision = await router.ClassifyAsync("synthetic alphabet request " + sample.Code, []);
            Check(decision is not null && decision.Mode == sample.Mode && decision.Domain == sample.Domain &&
                  decision.IsConfident, "Each structured code must preserve its mode/domain mapping: " + sample.Code);
        }

        var capture = handler.Requests[0];
        Check(capture.Endpoint.AbsoluteUri == "http://127.0.0.1:18180/v1/chat/completions",
            "The default router must use the already-running native endpoint.");
        using var request = JsonDocument.Parse(capture.Body);
        var body = request.RootElement;
        Check(!body.TryGetProperty("grammar", out _) &&
              body.GetProperty("response_format").GetProperty("type").GetString() == "json_object",
            "The local classifier must request bounded structured JSON.");
        Check(body.GetProperty("max_tokens").GetInt32() is >= 160 and <= 320,
            "The response budget must fit communication slots without forcing non-chat generation.");
        Check(body.GetProperty("temperature").GetInt32() == 0 && !body.GetProperty("stream").GetBoolean() &&
              !body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() &&
              body.GetProperty("cache_prompt").GetBoolean(), "The routing call must remain deterministic and lightweight.");
        var diagnostic = LastDiagnostic(paths);
        Check(diagnostic.GetProperty("failure").ValueKind == JsonValueKind.Null &&
              diagnostic.GetProperty("finishReason").GetString() == "stop" &&
              diagnostic.GetProperty("contentLength").GetInt32() == 9 &&
              diagnostic.GetProperty("httpStatus").GetInt32() == 200, "Successful diagnostic fields must be recorded.");
    }

    private static async Task StructuredCommunicationAsync()
    {
        const string request = "替我和通讯录里的测试对象甲接着聊，了解近况。";
        const string chat = """{"d":"W","comm":{"a":"chat","p":"wechat","r":"测试对象甲","m":0}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(chat)))))
        {
            var decision = await router.ClassifyAsync(request, []);
            Check(CommunicationIntentResolver.TryResolveFromSemantic(decision, request, out var resolved) &&
                  resolved.FastPathIntent is { Recipient: "测试对象甲" } &&
                  resolved.FastPathIntent.CommunicationGoal == request,
                "One semantic call must classify and preserve the complete chat goal and recipient.");
        }

        const string multiple = """{"d":"W","comm":{"a":"chat","p":"wechat","r":"测试对象甲","rs":["测试对象甲","测试对象乙"],"m":0,"tg":[{"r":"测试对象甲","g":"讨论工作进度","i":"同事"},{"r":"测试对象乙","g":"询问旅行计划","i":"朋友"}]}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(multiple)))))
        {
            var decision = await router.ClassifyAsync("分别与两个测试对象交流", []);
            Check(CommunicationIntentResolver.TryResolveFromSemantic(decision, "分别与两个测试对象交流", out var resolved) &&
                  resolved.FastPathIntent is null &&
                  resolved.Targets is { HasDistinctInstructionsForEveryTarget: true } &&
                  resolved.Targets.TargetInstructions[0].Goal == "讨论工作进度" &&
                  resolved.Targets.TargetInstructions[0].Persona == "同事" &&
                  resolved.Targets.TargetInstructions[1].Goal == "询问旅行计划" &&
                  resolved.Targets.TargetInstructions[1].Persona == "朋友",
                "Per-person goals and identities must stay separate; a multi-target payload cannot become one send.");
        }
        const string missingGoals = """{"d":"W","comm":{"a":"chat","p":"wechat","r":"测试对象甲","rs":["测试对象甲","测试对象乙"],"m":0}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(missingGoals)))))
        {
            var decision = await router.ClassifyAsync("两个不同的人，各聊各的", []);
            Check(!CommunicationIntentResolver.TryResolveFromSemantic(
                    decision, "两个不同的人，各聊各的", out _),
                "Missing individual goals must request a separate extraction, never authorize a multi-contact fast path.");
        }
        const string excluded = """{"d":"W","comm":{"a":"chat","p":"wechat","r":"测试对象甲","rs":["测试对象甲","测试对象乙"],"ex":["测试对象乙"],"tg":[{"r":"测试对象甲","g":"谈工作","i":"同事"},{"r":"测试对象乙","g":"谈生活","i":"朋友"}],"m":0}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(excluded)))))
        {
            var decision = await router.ClassifyAsync("只联系允许联系的人", []);
            Check(CommunicationIntentResolver.TryResolveFromSemantic(decision, "只联系允许联系的人", out var resolved) &&
                  resolved.Targets is { HasDistinctInstructionsForEveryTarget: false } &&
                  resolved.FastPathIntent is null,
                "An excluded named recipient must not be scheduled even with a proposed goal.");
        }
        const string discovery = """{"d":"W","comm":{"a":"discover","p":"soul","r":"unknown","n":true,"m":0}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(discovery)))))
        {
            var decision = await router.ClassifyAsync("找到合适的新对象", []);
            Check(CommunicationIntentResolver.TryResolveFromSemantic(decision, "找到合适的新对象", out var resolved) &&
                  resolved.Targets is { FindNewPeople: true, RequiresTargetSelection: true } &&
                  resolved.FastPathIntent is null,
                "Social discovery without an identified person must not become a send.");
        }
        // These held-out phrasings are absent from both model instructions and
        // application rules. Synthetic model replies verify transport/parsing
        // only; a live-model unseen-language accuracy score is still needed.
        var unseenOutcomeCases = new (string Request, string Outcome, SocialDiscoveryDesiredOutcome Expected)[]
        {
            ("把那边值得认识的几个人资料挑出来，先别帮我搭话。", "shortlist", SocialDiscoveryDesiredOutcome.CandidateShortlist),
            ("瞧瞧谁适合接触，能打个招呼的就先打个招呼。", "contact", SocialDiscoveryDesiredOutcome.ContactInitiated),
            ("去认识些新人，等对方回应了再接着聊，不要只发一句。", "conversation", SocialDiscoveryDesiredOutcome.ReciprocalConversation),
            ("想认真认识一个互相愿意成为伴侣的人，先从当地人里看看。", "relationship", SocialDiscoveryDesiredOutcome.RelationshipEstablished),
        };
        foreach (var sample in unseenOutcomeCases)
        {
            var response = JsonSerializer.Serialize(new
            {
                d = "W",
                comm = new { a = "discover", p = "douyin", r = "unknown", n = true, o = sample.Outcome, m = 0 },
            });
            var capture = new CaptureHandler(_ => Task.FromResult(Completion(response)));
            using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                NewPaths(), capture);
            var decision = await router.ClassifyAsync(sample.Request, []);
            Check(decision?.CommunicationTargets is { FindNewPeople: true } slots &&
                  slots.DesiredOutcome == sample.Expected &&
                  CommunicationIntentResolver.TryResolveFromSemantic(decision, sample.Request, out var resolved) &&
                  resolved.Targets?.DesiredOutcome == sample.Expected &&
                  resolved.FastPathIntent is null,
                "A held-out wording must preserve its model-provided outcome without fixed phrase routing: " + sample.Outcome);
            using var sent = JsonDocument.Parse(capture.Requests[0].Body);
            Check(sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!
                      .Contains("relationship=双方确认的恋爱关系", StringComparison.Ordinal) &&
                  sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!
                      .Contains(sample.Request, StringComparison.Ordinal),
                "The semantic model must receive the unmodified request and outcome schema.");
        }
        const string missingLiteral = """{"d":"W","comm":{"a":"send","p":"wechat","r":"测试对象甲","m":0}}""";
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion(missingLiteral)))))
        {
            var decision = await router.ClassifyAsync("对测试对象甲说一段长话", []);
            Check(!CommunicationIntentResolver.TryResolveFromSemantic(
                    decision, "对测试对象甲说一段长话", out _),
                "An omitted verbatim message must fall back to the lossless dedicated extractor.");
        }
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault,
                   NewPaths(), new CaptureHandler(_ => Task.FromResult(Completion("""{"d":"A","comm":{"a":"chat","p":"wechat","r":"测试对象甲"}}""")))))
        {
            var decision = await router.ClassifyAsync("只讨论怎样写聊天内容", []);
            Check(decision is { Mode: SemanticIntentMode.Answer, CommunicationTargets: null } &&
                  !CommunicationIntentResolver.TryResolveFromSemantic(decision, "只讨论怎样写聊天内容", out _),
                "Communication-looking text must not turn an answer classification into a send.");
        }
    }

    private static async Task ExternalCompatibilityAsync()
    {
        var responses = new Queue<HttpResponseMessage>([
            Completion(" \nW\t", finishReason: null),
            Completion("结果：M｜office｜88"),
        ]);
        var handler = new CaptureHandler(_ => Task.FromResult(responses.Dequeue()));
        using var router = new SemanticIntentRouter(
            AgentBackendConfiguration.External("configured-model", "https://configured.example/v1/"), NewPaths(), handler);
        Check(await router.ClassifyAsync("synthetic external one", []) is
            { Mode: SemanticIntentMode.Operate, Domain: SemanticIntentDomain.Chat },
            "An external endpoint may return a whitespace-wrapped code without finish_reason.");
        Check(await router.ClassifyAsync("synthetic external two", []) is
            { Mode: SemanticIntentMode.Mixed, Domain: SemanticIntentDomain.Office, Confidence: .88 },
            "An external endpoint's previous structured response must still parse.");
        foreach (var capture in handler.Requests)
        {
            using var request = JsonDocument.Parse(capture.Body);
            Check(capture.Endpoint.AbsoluteUri == "https://configured.example/v1/chat/completions" &&
                  request.RootElement.GetProperty("model").GetString() == "configured-model" &&
                  !request.RootElement.TryGetProperty("grammar", out _) &&
                  !request.RootElement.TryGetProperty("response_format", out _),
                "Do not assume grammar or schema support on an externally configured endpoint.");
        }
    }

    private static async Task InvalidResponsesAsync()
    {
        const string secret = "synthetic-private-user-text-that-must-never-be-logged";
        var samples = new (Func<HttpResponseMessage> Response, string Failure, string? Finish)[]
        {
            (() => Completion(" \n\t"), "empty_content", "stop"),
            (() => Completion("模式："), "invalid_decision", "stop"),
            (() => Completion("O|chat|96"), "invalid_decision", "stop"),
            (() => Completion("W " + secret), "invalid_decision", "stop"),
            (() => Completion("W", "length"), "truncated_response", "length"),
            (() => Completion("W", "content_filter"), "unexpected_finish_reason", "content_filter"),
            (() => Completion("W", "tool_calls"), "unexpected_finish_reason", "tool_calls"),
            (() => Completion(secret, secret), "invalid_decision", "other"),
            (() => Raw("{\"choices\":[]}"), "missing_choice", null),
            (() => Raw("null"), "missing_choice", null),
            (() => Raw("{\"choices\":[{\"message\":{},\"finish_reason\":\"stop\"}]}"), "missing_content", "stop"),
            (() => Raw("{\"choices\":[{\"message\":{\"content\":42},\"finish_reason\":\"stop\"}]}"), "invalid_content_type", "stop"),
            (() => Completion(null), "empty_content", "stop"),
            (() => Raw("invalid-json " + secret), "invalid_json", null),
            (() => Raw(secret, HttpStatusCode.BadRequest), "http_error", null),
        };
        foreach (var sample in samples)
        {
            var paths = NewPaths();
            var count = 0;
            var handler = new CaptureHandler(async cancellationToken =>
            {
                await Task.Delay(25, cancellationToken);
                return count++ == 0 ? sample.Response() : Completion("""{"d":"W"}""");
            });
            using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, paths, handler);
            Check(await router.ClassifyAsync(secret, []) is null,
                "An invalid or incomplete response must return no decision: " + sample.Failure);
            var diagnostic = LastDiagnostic(paths);
            Check(diagnostic.GetProperty("failure").GetString() == sample.Failure &&
                  diagnostic.GetProperty("finishReason").GetString() == sample.Finish &&
                  diagnostic.GetProperty("elapsedMilliseconds").GetInt64() >= 15,
                "Invalid-response diagnostics must retain failure, finish reason, and actual elapsed time: " + sample.Failure);
            Check(!File.ReadAllText(DiagnosticPath(paths)).Contains(secret, StringComparison.Ordinal),
                "Diagnostics must not persist request text, model output, exception content, or arbitrary finish reasons.");
            Check(await router.ClassifyAsync(secret, []) is { Domain: SemanticIntentDomain.Chat } && handler.Requests.Count == 2,
                "A failed classification must not enter the cache: " + sample.Failure);
        }

        var transportPaths = NewPaths();
        using var transportRouter = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, transportPaths,
            new CaptureHandler(_ => throw new HttpRequestException(secret)));
        Check(await transportRouter.ClassifyAsync(secret, []) is null &&
              LastDiagnostic(transportPaths).GetProperty("failure").GetString() == "transport_error" &&
              !File.ReadAllText(DiagnosticPath(transportPaths)).Contains(secret, StringComparison.Ordinal),
            "Transport failures must remain distinguishable without storing exception messages.");
    }

    private static async Task CacheAndContextAsync()
    {
        var paths = NewPaths();
        var handler = new CaptureHandler(_ => Task.FromResult(Completion("""{"d":"W"}""")));
        using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, paths, handler);
        ConversationMessageSnapshot[] context =
        [
            new("user", "discarded-older-message", []),
            new("user", "current-person  and\nplatform", []),
            new("assistant", new string('z', 140), []),
            new("user", "latest context", []),
        ];
        const string request = "same exact request?";
        await router.ClassifyAsync(request, context);
        Check(await router.ClassifyAsync(request, context) is { Source: "local-semantic-cache", ElapsedMilliseconds: 0 } &&
              handler.Requests.Count == 1, "An exact request/context pair should reuse the confident decision.");
        var changed = context.ToArray();
        changed[^1] = new("user", "a different context", []);
        await router.ClassifyAsync(request, changed);
        await router.ClassifyAsync(request + " ", context);
        await router.ClassifyAsync(request.Replace("?", "!"), context);
        Check(handler.Requests.Count == 4, "Changed context, spacing, and punctuation must not collide in the cache.");
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Check(!prompt.Contains("discarded-older-message", StringComparison.Ordinal) &&
              prompt.Contains("用户：current-person and platform", StringComparison.Ordinal) &&
              prompt.Contains("助手：" + new string('z', 119) + "…", StringComparison.Ordinal) &&
              prompt.EndsWith("当前要求：" + request, StringComparison.Ordinal),
            "Recent context selection, compacting, truncation, and the current request must remain unchanged.");
        Check(await router.ClassifyAsync(" \n\t", []) is null && handler.Requests.Count == 4,
            "An empty request must not send a classification request.");
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await ExpectCanceledAsync(() => router.ClassifyAsync(request, context, alreadyCanceled.Token));
        Check(handler.Requests.Count == 4, "Cancellation must be respected even for a cache hit.");
    }

    private static async Task CancellationAsync()
    {
        var timeoutPaths = NewPaths();
        var timeoutHandler = new CaptureHandler(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completion("""{"d":"W"}""");
        });
        using (var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, timeoutPaths,
                   timeoutHandler, TimeSpan.FromMilliseconds(60)))
        {
            Check(await router.ClassifyAsync("synthetic timeout", []) is null,
                "The router's deadline should produce an inconclusive result.");
            var diagnostic = LastDiagnostic(timeoutPaths);
            Check(diagnostic.GetProperty("timedOut").GetBoolean() &&
                  diagnostic.GetProperty("failure").GetString() == "timeout" &&
                  diagnostic.GetProperty("elapsedMilliseconds").GetInt64() >= 30,
                "A timeout must retain measured duration and be distinguishable from invalid output.");
        }

        var canceledPaths = NewPaths();
        var count = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledHandler = new CaptureHandler(async token =>
        {
            if (count++ == 0)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Completion("""{"d":"W"}""");
        });
        using var canceledRouter = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, canceledPaths,
            canceledHandler, TimeSpan.FromSeconds(5));
        using var callerCancellation = new CancellationTokenSource();
        var pending = canceledRouter.ClassifyAsync("synthetic cancellation", [], callerCancellation.Token);
        await started.Task;
        callerCancellation.Cancel();
        await ExpectCanceledAsync(() => pending);
        Check(!File.Exists(DiagnosticPath(canceledPaths)), "Caller cancellation must not be recorded as a timeout or failure.");
        Check(await canceledRouter.ClassifyAsync("synthetic cancellation", []) is { Domain: SemanticIntentDomain.Chat } &&
              canceledHandler.Requests.Count == 2, "Cancellation must not cache a decision or poison the next call.");
    }

    private static async Task ExpectCanceledAsync(Func<Task<SemanticIntentDecision?>> action)
    {
        try
        {
            await action();
            throw new InvalidDataException("Caller cancellation must propagate.");
        }
        catch (OperationCanceledException) { }
    }

    private static HuiTianPaths NewPaths() => new(Path.Combine(@"D:\HuiTian\Cache\Tests", "router-contract-" + Guid.NewGuid().ToString("N")));

    private static string DiagnosticPath(HuiTianPaths paths) => Path.Combine(paths.AppState, "semantic-intent.jsonl");

    private static JsonElement LastDiagnostic(HuiTianPaths paths)
    {
        using var diagnostic = JsonDocument.Parse(File.ReadLines(DiagnosticPath(paths)).Last());
        return diagnostic.RootElement.Clone();
    }

    private static HttpResponseMessage Completion(string? content, string? finishReason = "stop") => Raw(
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = finishReason } } }));

    private static HttpResponseMessage Raw(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class CaptureHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(Uri Endpoint, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await respond(cancellationToken);
        }
    }
}
