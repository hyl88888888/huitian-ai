using System.Net;
using System.Text;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class CommunicationResolverRegression
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool ok, string detail) { if (!ok) throw new InvalidDataException(detail); passed++; }
        const string goal = "沿用刚才提到的身份，找通讯录里的测试对象甲接着谈。先听对方的想法，再谈延期；报价不许变，不要擅自承诺时间。";
        const string compact = """{"a":"chat","p":"Weixin","r":"测试对象甲","m":0}""";
        Check(CommunicationIntentResolver.TryParse(compact, out var chat, goal), "compact semantic chat rejected");
        Check(chat.PlatformName == "微信" && chat.Recipient == "测试对象甲", "semantic application identity/recipient lost");
        Check(chat.CommunicationGoal == goal && chat.CommunicationPlan!.OriginalGoal == goal,
            "full goal and later constraints must remain lossless without model repetition");
        Check(chat.ContinuousConversationRequested && chat.CommunicationPlan!.RequestedDuration is null,
            "unspecified duration must not become a user deadline");
        Check(chat.Message == string.Empty && CommunicationOpeningPolicy.RequiresSemanticComposition(chat.CommunicationPlan!),
            "private request must not become an outgoing opening message");
        Check(!CommunicationIntentResolver.TryParse(compact, out _), "missing goal with no request must fail closed");
        Check(CommunicationIntentResolver.TryParse("""{"a":"goal","p":"wechat","r":"测试对象乙","m":0}""", out var once, goal) &&
            !once.ContinuousConversationRequested && once.MessageComposedFromGoal, "one-off purpose must remain one-off");
        Check(CommunicationIntentResolver.TryParse("""{"a":"send","p":"qq","r":"测试对象乙","v":"  A\nB  "}""", out var literal, goal) &&
            literal.Message == "  A\nB  " && !literal.MessageComposedFromGoal, "dictated text must remain exact");
        Check(!CommunicationIntentResolver.TryParse("""{"a":"send","p":"qq","r":"测试对象乙"}""", out _, goal),
            "missing literal payload must not be replaced with the user's instruction");
        Check(CommunicationIntentResolver.TryParse("""{"a":"file","p":"WeChat","r":"测试对象乙"}""", out var file, goal) &&
            file.ReferencesFiles && file.Message is null, "existing file task must not synthesize a message");
        foreach (var invalid in new[] {
            """{"a":"chat","p":"WeCom","r":"甲"}""",
            """{"a":"chat","p":"wechat","r":"unknown"}""",
            """{"a":"chat","p":"wechat","r":""}""",
            """{"a":"unknown","p":"wechat","r":"甲"}""",
            """{"a":"chat","p":"wechat","r":123}""" })
            Check(!CommunicationIntentResolver.TryParse(invalid, out _, goal), "unresolved/invalid destination accepted");
        Check(CommunicationIntentResolver.TryParse("""{"a":"chat","p":"qq","r":"@current","m":20}""", out var current, goal) &&
            current.UseCurrentConversation && current.CommunicationPlan!.RequestedDuration == TimeSpan.FromMinutes(20),
            "current conversation or explicit duration lost");
        Check(!CommunicationOpeningPolicy.ComposeSafeImmediateOpening(current.CommunicationPlan!).Contains("@current"),
            "internal recipient marker leaked into opening");

        const string socialDiscovery = """{"a":"discover","p":"soul","ps":["soul","douyin"],"r":"unknown","n":true,"o":"conversation","ex":["家人","现有关系"],"c":"找适合自然聊天的新对象","m":0}""";
        Check(CommunicationIntentResolver.TryParseTargetSlots(socialDiscovery, out var discovery) &&
            discovery.Action == "discover" && discovery.Platforms.SequenceEqual(["Soul", "抖音"]) &&
            discovery.TargetNames.Count == 0 && discovery.FindNewPeople && discovery.RequiresTargetSelection &&
            discovery.ExcludedNames.SequenceEqual(["家人", "现有关系"]) &&
            discovery.DesiredOutcome == SocialDiscoveryDesiredOutcome.ReciprocalConversation,
            "Soul/Douyin discovery slots or relationship exclusions lost");
        Check(CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":"relationship"}""",
                out var relationship) &&
              relationship.DesiredOutcome == SocialDiscoveryDesiredOutcome.RelationshipEstablished &&
              CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":"shortlist"}""",
                out var shortlist) &&
              shortlist.DesiredOutcome == SocialDiscoveryDesiredOutcome.CandidateShortlist &&
              CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":"contact"}""",
                out var contact) &&
              contact.DesiredOutcome == SocialDiscoveryDesiredOutcome.ContactInitiated &&
              CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":"unknown"}""",
                out var unknown) &&
              unknown.DesiredOutcome == SocialDiscoveryDesiredOutcome.Unspecified &&
              !CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":42}""", out _) &&
              !CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"douyin","r":"unknown","n":true,"o":"invented-tier"}""", out _),
            "new-person outcome tiers must be structured and fail closed on malformed data");
        Check(!CommunicationIntentResolver.TryParse(socialDiscovery, out _, goal),
            "a discovery request must not become a real chat send");
        const string multipleContacts = """{"a":"chat","p":"douyin","r":"用户甲","rs":["用户甲","用户乙"],"m":20}""";
        Check(CommunicationIntentResolver.TryParseTargetSlots(multipleContacts, out var multiple) &&
            multiple.TargetNames.SequenceEqual(["用户甲", "用户乙"]) &&
            multiple.RequiresTargetSelection && multiple.RequestedDuration == TimeSpan.FromMinutes(20),
            "multiple named targets must remain separate");
        Check(!CommunicationIntentResolver.TryParse(multipleContacts, out _, goal) &&
            !CommunicationIntentResolver.TryParse(
                """{"a":"chat","p":"wechat","r":"用户甲","rs":["用户甲","用户乙"],"m":20}""",
                out _, goal),
            "a single-recipient fast path must reject multiple contacts on every platform");
        Check(CommunicationIntentResolver.TryParse(
                """{"a":"chat","p":"wechat","r":"用户甲","rs":["用户甲"],"m":20}""",
                out var singleFromList, goal) && singleFromList.Recipient == "用户甲",
            "one named target in a list should keep the existing fast path");
        Check(!CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"chat","p":"wechat","r":"用户甲","rs":["用户乙"]}""", out _),
            "contradictory singular and list targets must fail closed");
        Check(!CommunicationIntentResolver.TryParse(
                """{"a":"chat","p":"wechat","r":"用户甲","ex":["用户甲"]}""", out _, goal),
            "excluded target must not become a send destination");
        Check(CommunicationIntentResolver.TryParseTargetSlots(
                """{"a":"discover","p":"other","site":"某交友网站","r":"unknown","n":true}""",
                out var otherSite) && otherSite.Platforms.SequenceEqual(["某交友网站"]),
            "an explicitly named other site should be retained for discovery");

        var directory = Path.Combine(@"D:\HuiTian\Cache\Tests", "communication-contract-" + Guid.NewGuid().ToString("N"));
        var config = new AgentBackendConfiguration("huitian-local", "fixture-only", "http://127.0.0.1:1/v1", false);
        var handler = new FixtureHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Check(root.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains(goal), "request was truncated before model");
            Check(root.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("n=true时增加o字段"), "secondary extractor must request the final social outcome tier");
            Check(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() == false, "slot extraction must not request hidden reasoning");
            await Task.Delay(15, token);
            return Reply(compact, "stop");
        });
        using (var resolver = new CommunicationIntentResolver(config, handler, directory))
            Check((await resolver.ResolveAsync(goal, [], false))?.CommunicationGoal == goal, "production HTTP→semantic plan path failed");
        var log = File.ReadAllText(Path.Combine(directory, "communication-intent.jsonl"));
        using (var row = JsonDocument.Parse(log.Trim()))
        {
            Check(row.RootElement.GetProperty("outcome").GetString() == "resolved" &&
                row.RootElement.GetProperty("elapsedMilliseconds").GetInt64() > 0, "resolution timing missing");
            Check(!log.Contains(goal) && !log.Contains("测试对象"), "diagnostics must not store private goals/recipients");
        }
        using (var truncated = new CommunicationIntentResolver(config,
            new FixtureHandler((_, _) => Task.FromResult(Reply(compact, "length"))), directory))
            Check(await truncated.ResolveAsync(goal, [], false) is null, "truncated model response must not trigger send");
        var discoveryDirectory = Path.Combine(directory, "discovery");
        using (var resolver = new CommunicationIntentResolver(config,
            new FixtureHandler((_, _) => Task.FromResult(Reply(socialDiscovery, "stop"))),
            discoveryDirectory))
        {
            var resolved = await resolver.ResolveDetailedAsync("去 Soul 和抖音找新的人聊天，避开家人", [], false);
            Check(resolved.Targets is { FindNewPeople: true } && resolved.FastPathIntent is null,
                "live slot extraction must expose discovery without inventing a sender");
        }
        Check(File.ReadAllText(Path.Combine(discoveryDirectory, "communication-intent.jsonl"))
                .Contains("target_slots_only", StringComparison.Ordinal),
            "unsupported discovery must be logged as slots only");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using (var resolver = new CommunicationIntentResolver(config, new FixtureHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }), directory))
        {
            var pending = resolver.ResolveAsync(goal, [], false, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            try { await pending; Check(false, "user cancellation swallowed"); }
            catch (OperationCanceledException) { Check(true, "cancelled"); }
        }
        Console.WriteLine($"PASS {passed} communication semantic contract checks (fixture HTTP, no model accuracy claim or desktop sends).");
    }

    private static HttpResponseMessage Reply(string content, string finish) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new {
            message = new { content }, finish_reason = finish } } }), Encoding.UTF8, "application/json"),
    };
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
