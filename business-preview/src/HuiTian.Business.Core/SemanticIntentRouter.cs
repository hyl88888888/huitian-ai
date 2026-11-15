using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuiTian.Business.Core;

public sealed class SemanticIntent
{
    public string Mode { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Goal { get; set; } = "";
    public List<string> CapabilityGroups { get; set; } = [];
}

/// <summary>Experimental first-stage semantic routing, with no tool catalog or argument generation.</summary>
public sealed class SemanticIntentRouter(IModelClient model)
{
    private static readonly string[] Groups = ["files", "documents", "browser", "desktop"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonElement OutputSchema = MakeSchema();

    public async Task<SemanticIntent> RouteAsync(string request, IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request))
            return new() { Mode = "clarify", Answer = "请输入要咨询或执行的内容。" };
        const string instructions = """
            你是回天的语义意图分流器，只解释用户想得到什么。理解完整请求和历史，最新要求优先，处理指代、否定和任务切换。只输出JSON，不写思考或工具参数。
            你没有当前工具、权限、登录或软件状态信息，不裁定操作是否可用。可行性由下一阶段和实际工具核查；不得因为猜测无法访问或没有权限而改为clarify。
            mode=answer：只需在聊天回复中完成的闲聊、知识解释、教程、内容创作或改写；answer直接交付内容，capabilityGroups为空。只写回复无需创建文件或操作电脑。
            mode=task：需要查询或改变外部资源，或在实际文件、网页、应用中实现最终目的；answer为空，选择所需能力组，可选多个。即使具体支持尚未核查，也按用户意图归task，不承诺能执行。
            mode=clarify：结合历史仍无法解析关键目标或关键指代，answer问清缺失的目标，capabilityGroups为空。可由后续观察取得的执行信息不在此阶段要求用户补齐。
            组：files=定位和读取用户本地文件；documents=创建或修改实际文档、表格、演示文件；browser=网络查询及网页内观察或操作；desktop=本机应用和窗口内的操作。
            禁止声称已执行，禁止编写步骤或参数。task若无法确定组就给空组，不猜。未知事实不得编造；goal概括用户最终目的，不能替换为中间步骤或缩减目标。
            字段仅为mode、answer、goal、capabilityGroups。回答简明，goal只写短语。
            """;
        var messages = BuildMessages(instructions, request, history);
        var response = model is IStructuredModelClient structured
            ? await structured.CompleteStructuredAsync(messages, OutputSchema, 1024, cancellationToken)
            : await model.CompleteAsync(messages, true, 1024, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new PlanValidationException("意图路由必须返回一个对象。");
            var required = new HashSet<string>(["mode", "answer", "goal", "capabilityGroups"], StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!required.Remove(property.Name))
                    throw new PlanValidationException("意图路由包含重复或未声明字段。");
            if (required.Count != 0) throw new PlanValidationException("意图路由缺少必填字段。");
            var intent = JsonSerializer.Deserialize<SemanticIntent>(response, JsonOptions)
                ?? throw new PlanValidationException("意图路由没有返回内容。");
            Validate(intent);
            return intent;
        }
        catch (JsonException ex) { throw new PlanValidationException("模型未返回有效的意图路由JSON。", ex); }
    }

    private static void Validate(SemanticIntent intent)
    {
        if (intent.Mode is not ("answer" or "task" or "clarify") || intent.Answer is null || intent.Goal is null ||
            intent.CapabilityGroups is null || intent.Goal.Length > 160 || intent.Answer.Length > 4000)
            throw new PlanValidationException("意图路由类型、字段或长度无效。");
        if (intent.CapabilityGroups.Count > Groups.Length ||
            intent.CapabilityGroups.Distinct(StringComparer.Ordinal).Count() != intent.CapabilityGroups.Count ||
            intent.CapabilityGroups.Any(group => !Groups.Contains(group, StringComparer.Ordinal)))
            throw new PlanValidationException("意图路由包含未知或重复能力组。");
        if (intent.Mode == "task")
        {
            if (string.IsNullOrWhiteSpace(intent.Goal) || intent.Answer.Length != 0)
                throw new PlanValidationException("执行意图需要简短目标，不能提前给出执行答复。");
        }
        else if (string.IsNullOrWhiteSpace(intent.Answer) || intent.CapabilityGroups.Count != 0)
            throw new PlanValidationException("知识答复或澄清需要实际答复，且不能安排能力组。");
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(string instructions, string request,
        IReadOnlyList<ChatMessage> history)
    {
        var messages = new List<ChatMessage> { new("system", instructions) };
        foreach (var item in history)
            if (!string.IsNullOrWhiteSpace(item.Content))
                messages.Add(new(item.Role == "assistant" ? "assistant" : "user", item.Content));
        if (messages.Count == 1 || messages[^1].Role != "user" || messages[^1].Content != request)
            messages.Add(new("user", request));
        static int Cost(string content)
        {
            var ascii = content.Count(c => c <= 127);
            return (int)Math.Ceiling((ascii / 3.0 + content.Length - ascii) * 1.15) + 12;
        }
        if (messages.Sum(m => Cost(m.Content)) > 6200)
            throw new ContextLimitException("完整请求与历史超过轻量路由的本地上下文容量，请缩短本轮材料或开始较短对话；当前目标未被截断。");
        return messages;
    }

    private static JsonElement MakeSchema()
    {
        static JsonArray Values(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        var alternatives = new JsonArray();
        foreach (var mode in new[] { "answer", "task", "clarify" })
        {
            var task = mode == "task";
            alternatives.Add(new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = Values(mode) },
                    ["answer"] = task
                        ? new JsonObject { ["type"] = "string", ["enum"] = Values("") }
                        : new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4000 },
                    ["goal"] = new JsonObject { ["type"] = "string", ["minLength"] = task ? 1 : 0, ["maxLength"] = 160 },
                    ["capabilityGroups"] = new JsonObject
                    {
                        ["type"] = "array", ["maxItems"] = task ? Groups.Length : 0,
                        ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Values(Groups) }
                    }
                },
                ["required"] = Values("mode", "answer", "goal", "capabilityGroups")
            });
        }
        return JsonSerializer.SerializeToElement(new JsonObject { ["anyOf"] = alternatives });
    }
}
