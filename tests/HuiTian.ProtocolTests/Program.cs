using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HuiTian.Core;
using HuiTian.Infrastructure;
using HuiTian.App;

if (args.Contains("--safe-local-task")) { await SafeLocalTaskProbe.RunAsync(); return; }
if (args.Contains("--character-reveal")) { CharacterRevealRegression.Run(); return; }
LocalBackendProxyRegression.Run();
const string instructions = "固定身份指令";
Assert(PersonalBrainModelPolicy.SelectedTier == CapabilityTierId.Huang,
    "the personal-use edition must keep its existing local model package instead of auto-selecting a hardware tier");
if (args.Contains("--personal-model-ui"))
{
    Exception? uiFailure = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var application = new HuiTian.App.App();
            application.InitializeComponent();
            var window = new MainWindow(); // not shown or loaded: no model or hardware starts
            var tierPanel = (StackPanel)window.FindName("SettingsTierItems");
            Assert(tierPanel.Visibility == Visibility.Collapsed && tierPanel.Children.Count == 0,
                "personal-use settings must not show customer hardware-tier cards");
            var firstRun = (Grid)window.FindName("AdaptationView");
            Assert(firstRun.Visibility == Visibility.Collapsed,
                "personal-use startup must not show the hardware grading screen");
        }
        catch (Exception exception)
        {
            uiFailure = exception;
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (uiFailure is not null) throw uiFailure;
    Console.WriteLine("PASS: personal model UI hides first-run grading and all four hardware tiers");
    return;
}
if (args.Contains("--delivery-probe"))
{
    await LiveToolContractProbe.RunAsync(delivery: true, startLocalCore: args.Contains("--start-local-core"));
    return;
}
if (args.Contains("--live-tool-contract"))
{
    await LiveToolContractProbe.RunAsync();
    return;
}
if (args.Contains("--execution-boundary"))
{
    ScreenObservationBoundary.Run();
    await ExecutionBoundaryRegression.RunAsync();
    return;
}
if (args.Contains("--context-compaction"))
{
    await ContextCompactionRegression.RunAsync();
    return;
}
Assert(CodexAppServerClient.RequiresThreadToolUpgrade(AgentTaskRoute.DirectAnswer, AgentTaskRoute.Composite) &&
       CodexAppServerClient.RequiresThreadToolUpgrade(AgentTaskRoute.DirectAnswer, AgentTaskRoute.FileWork) &&
       !CodexAppServerClient.RequiresThreadToolUpgrade(AgentTaskRoute.DirectAnswer, AgentTaskRoute.DirectAnswer) &&
       !CodexAppServerClient.RequiresThreadToolUpgrade(AgentTaskRoute.Composite, AgentTaskRoute.DirectAnswer),
    "changing a tool-free answer into an operation needs a genuinely tool-capable thread, not a label change");
Assert(!CodexAppServerClient.IsCurrentThreadNotification("new", "old") &&
       CodexAppServerClient.IsCurrentThreadNotification("new", "new") &&
       CodexAppServerClient.IsCurrentThreadNotification("new", null),
    "late events from an abandoned thread must not complete, clear or recover the new task");

await using (var notificationClient = new CodexAppServerClient(new HuiTianPaths()))
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.NonPublic;
    var turnField = typeof(CodexAppServerClient).GetField("_currentTurnId", flags)!;
    var superseded = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(CodexAppServerClient)
        .GetField("_supersededTurnIds", flags)!.GetValue(notificationClient)!;
    var notify = typeof(CodexAppServerClient).GetMethod("HandleNotification", flags)!;
    var received = new List<AgentActivity>();
    notificationClient.ActivityReceived += (_, e) => received.Add(e.Activity);
    typeof(CodexAppServerClient).GetProperty("CurrentThreadId")!.SetValue(notificationClient, "same-thread");
    turnField.SetValue(notificationClient, "old-turn");
    superseded["old-turn"] = 0;
    using var oldCompleted = JsonDocument.Parse("""
        {"params":{"threadId":"same-thread","turn":{"id":"old-turn","status":"interrupted"}}}
        """);
    notify.Invoke(notificationClient, ["turn/completed", oldCompleted.RootElement]);
    Assert(received.Count == 0 && superseded.IsEmpty && !notificationClient.HasActiveTurn,
        "an internally replaced answer must retire without publishing a stop result or retaining its temporary marker");

    turnField.SetValue(notificationClient, "new-turn");
    notify.Invoke(notificationClient, ["turn/completed", oldCompleted.RootElement]);
    using var oldError = JsonDocument.Parse("""
        {"params":{"turnId":"old-turn","willRetry":false,"error":{"message":"late old failure"}}}
        """);
    notify.Invoke(notificationClient, ["error", oldError.RootElement]);
    Assert(received.Count == 0 && (string?)turnField.GetValue(notificationClient) == "new-turn",
        "duplicate old terminal events and errors without a thread id must not affect the replacement turn");

    using var userStopped = JsonDocument.Parse("""
        {"params":{"threadId":"same-thread","turn":{"id":"new-turn","status":"interrupted"}}}
        """);
    notify.Invoke(notificationClient, ["turn/completed", userStopped.RootElement]);
    Assert(received.Count == 1 && received[0].Status == AgentActivityStatus.Failed &&
           received[0].TurnId == "new-turn" && !notificationClient.HasActiveTurn,
        "a real user stop must still publish a failed terminal result for its own turn");
}
if (args.Contains("--superseded-events"))
{
    Console.WriteLine("PASS: superseded terminal isolation, late duplicate/error isolation, and real user stop");
    return;
}

var addBrainArguments = typeof(LocalBrainServer).GetMethod(
    "AddArguments",
    System.Reflection.BindingFlags.NonPublic |
    System.Reflection.BindingFlags.Static)
    ?? throw new MissingMethodException(nameof(LocalBrainServer), "AddArguments");
var brainStartInfo = new System.Diagnostics.ProcessStartInfo();
addBrainArguments.Invoke(
    null,
    [
        brainStartInfo,
        new LocalBrainInstallation(
            CapabilityTierId.Huang,
            "brain.bin",
            "vision.bin",
            8192,
            0),
        false,
    ]);
var brainArguments = brainStartInfo.ArgumentList.ToArray();
var imageTokensIndex = Array.IndexOf(brainArguments, "--image-min-tokens");
var reasoningIndex = Array.IndexOf(brainArguments, "--reasoning");
var reasoningBudgetIndex = Array.IndexOf(brainArguments, "--reasoning-budget");
Assert(
    imageTokensIndex >= 0 &&
    imageTokensIndex + 1 < brainArguments.Length &&
    brainArguments[imageTokensIndex + 1] == "1024",
    "the visual runtime must meet Qwen-VL's minimum grounding token budget");
Assert(
    reasoningIndex >= 0 &&
    reasoningIndex + 1 < brainArguments.Length &&
    brainArguments[reasoningIndex + 1] == "off" &&
    reasoningBudgetIndex >= 0 &&
    reasoningBudgetIndex + 1 < brainArguments.Length &&
    brainArguments[reasoningBudgetIndex + 1] == "0",
    "the product runtime must suppress hidden think monologues before tool execution");

var payload = $$"""
{
  "instructions": "{{instructions}}",
  "input": [
    {
      "type": "message",
      "role": "developer",
      "content": [
        { "type": "input_text", "text": "{{instructions}}" },
        { "type": "input_text", "text": "{{instructions}}" }
      ]
    },
    {
      "type": "message",
      "role": "user",
      "content": [
        { "type": "input_image", "image_url": "data:image/png;base64,old" }
      ]
    },
    {
      "type": "function_call_output",
      "call_id": "call-1",
      "output": [
        { "type": "input_text", "text": "已观察窗口" },
        { "type": "input_image", "image_url": "data:image/png;base64,new", "detail": "high" }
      ]
    }
  ],
  "reasoning": { "effort": "high" },
  "include": ["reasoning.encrypted_content"]
}
""";

var normalized = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(payload)))!.AsObject();
Assert(normalized["instructions"] is null, "instructions should be merged");
Assert(normalized["include"] is null, "unsupported include should be removed");
Assert(
    normalized["reasoning"]?["effort"]?.GetValue<string>() == "xhigh",
    "the local Responses bridge must normalize Codex high to supported xhigh");

var input = normalized["input"]!.AsArray();
var developerTextCount = input[0]!["content"]!.AsArray()
    .Count(item => item?["text"]?.GetValue<string>() == instructions);
Assert(developerTextCount == 1, "developer instructions should not be duplicated");

var output = input.OfType<JsonObject>()
    .Single(item => item["type"]?.GetValue<string>() == "function_call_output")["output"];
Assert(output is JsonValue && output.GetValue<string>() == "已观察窗口",
    "tool output should be plain input text");

var images = input.OfType<JsonObject>()
    .SelectMany(item => item["content"] is JsonArray content
        ? content.OfType<JsonObject>()
        : [])
    .Where(item => item["type"]?.GetValue<string>() == "input_image")
    .ToArray();
Assert(images.Any(item => item["image_url"]?.GetValue<string>()?.EndsWith("new") == true),
    "latest visual result should remain available to the vision model");
Assert(images.Length <= 2, "historical images should be bounded");
var repeatedObservation = new string('窗', 5_000);
var observationPayload = new JsonObject
{
    ["input"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = "old-observation",
            ["output"] = "当前窗口：Soul App - Google Chrome\n" + repeatedObservation,
        },
        new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = "latest-observation",
            ["output"] = "当前窗口：Soul App - Google Chrome\n" + repeatedObservation,
        },
    },
};
var boundedObservation = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(observationPayload.ToJsonString())))!.AsObject();
var boundedOutputs = boundedObservation["input"]!.AsArray().OfType<JsonObject>()
    .Where(item => item["type"]?.GetValue<string>() == "function_call_output")
    .Select(item => item["output"]!.GetValue<string>())
    .ToArray();
Assert(boundedOutputs[0].Length < 240 && boundedOutputs[0].Contains("旧观察已折叠", StringComparison.Ordinal),
    "stale accessibility snapshots should be folded instead of replayed into every local-model step");
Assert(boundedOutputs[1].Length <= 3_250 && boundedOutputs[1].StartsWith("当前窗口：", StringComparison.Ordinal),
    "the most recent accessibility snapshot should remain available but bounded");

var checkpointPayload = new JsonObject
{
    ["input"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = "You are performing a CONTEXT CHECKPOINT COMPACTION. Create a handoff summary.",
                },
            },
        },
    },
};
var boundedCheckpoint = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(checkpointPayload.ToJsonString())))!.AsObject();
Assert(boundedCheckpoint["max_output_tokens"]?.GetValue<int>() == 192,
    "context checkpoint generation should have a finite local-model output budget");
Assert(
    normalized["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false,
    "route-less and follow-up Responses requests must never re-enable hidden model thinking");

var routeLessProductPayload = """
{
  "input": [
    {
      "type": "message",
      "role": "developer",
      "content": [{ "type": "input_text", "text": "<permissions instructions>repeated generic primer</permissions instructions>" }]
    },
    {
      "type": "function_call_output",
      "call_id": "follow-up",
      "output": "file created"
    }
  ],
  "tools": [{ "type": "function", "name": "huitian_read_text_file" }]
}
""";
var routeLessProductRequest = JsonNode.Parse(
    LocalResponsesBridge.NormalizeResponsesRequest(
        Encoding.UTF8.GetBytes(routeLessProductPayload)))!.AsObject();
Assert(
    routeLessProductRequest["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false &&
    !routeLessProductRequest.ToJsonString().Contains("repeated generic primer", StringComparison.Ordinal),
    "compacted product follow-ups must retain the no-thinking contract and drop duplicated permission prose even without a route marker");

Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "没有在联系人结果中找到目标。",
        ResultCode: "not_found")) == ChatFastPathStatus.RecipientNotFound,
    "a missing recipient must not be collapsed into the generic send failure");
Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "页面中识别出多个相近文字行。",
        ResultCode: "ambiguous")) == ChatFastPathStatus.RecipientAmbiguous,
    "an ambiguous local page reading must retain its own user-facing status");
Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "打开后的标题不一致。",
        ResultCode: "title_mismatch")) == ChatFastPathStatus.TargetMismatch,
    "a title mismatch must be distinguishable from a duplicate recipient");
Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "本机文字识别组件不可用。",
        ResultCode: "ocr_unavailable")) == ChatFastPathStatus.RecognitionUnavailable,
    "a missing recognition component must retain an actionable status");
var unsupportedChatPlatform = new DynamicToolResult(
    false,
    "平台未适配。",
    ResultCode: "unsupported_platform");
Assert(
    WindowsChatFastPath.MapStatus(unsupportedChatPlatform) == ChatFastPathStatus.UnsupportedPlatform &&
    WindowsChatFastPath.ToUserSafeDetail(unsupportedChatPlatform).Contains("尚未发送", StringComparison.Ordinal),
    "an unsupported chat platform must not be mistaken for a transient sending failure");
Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "快速核对超过时限。",
        ResultCode: "ocr_timeout")) == ChatFastPathStatus.TimedOut,
    "a fast-path timeout must not be presented as recipient ambiguity");
Assert(
    WindowsChatFastPath.MapStatus(new DynamicToolResult(
        false,
        "<Objs><S S=\"Error\">Add-Type compiler output</S></Objs>",
        ResultCode: "ocr_failed")) == ChatFastPathStatus.RecognitionUnavailable,
    "an OCR helper startup failure must have an actionable product status");
var rawPowerShellFailure = "<Objs><S S=\"Error\">Add-Type : Regex PowerShell CLIXML</S></Objs>";
var safeFastPathDetail = WindowsChatFastPath.ToUserSafeDetail(new DynamicToolResult(
    false,
    rawPowerShellFailure,
    ResultCode: "failed"));
Assert(
    !safeFastPathDetail.Contains("<Objs", StringComparison.OrdinalIgnoreCase) &&
    !safeFastPathDetail.Contains("Add-Type", StringComparison.OrdinalIgnoreCase) &&
    !safeFastPathDetail.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) &&
    !safeFastPathDetail.Contains("CLIXML", StringComparison.OrdinalIgnoreCase),
    "raw helper output must never enter the product-facing fast-path detail");
var safeMainWindowReason = MainWindow.BuildSafeChatFailureReason(rawPowerShellFailure);
Assert(
    !safeMainWindowReason.Contains("<Objs", StringComparison.OrdinalIgnoreCase) &&
    !safeMainWindowReason.Contains("Add-Type", StringComparison.OrdinalIgnoreCase) &&
    !safeMainWindowReason.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) &&
    !safeMainWindowReason.Contains("CLIXML", StringComparison.OrdinalIgnoreCase),
    "the final UI boundary must reject technical XML and compiler output");
var redactedFastPathLog = WindowsChatFastPath.RedactDiagnostic(
    "InvalidOperationException while sending 你好老板 to 无良老板 from D:\\私人\\报价.xlsx",
    ["你好老板", "无良老板", @"D:\私人\报价.xlsx", "报价.xlsx"]);
Assert(
    !redactedFastPathLog.Contains("你好老板", StringComparison.Ordinal) &&
    !redactedFastPathLog.Contains("无良老板", StringComparison.Ordinal) &&
    !redactedFastPathLog.Contains("报价.xlsx", StringComparison.Ordinal),
    "local fast-path diagnostics must redact recipient, message text, and attachment names");
var redactedOcrLog = WindowsUiAutomation.RedactWeixinOcrDiagnostic(
    "Add-Type failed while locating 无良老板",
    "无良老板");
Assert(
    !redactedOcrLog.Contains("无良老板", StringComparison.Ordinal),
    "local OCR diagnostics must redact the requested contact name");

Assert(
    ConversationPolicy.Classify("帮我给这段文案想一个反认知标题") ==
    AgentTaskRoute.DirectAnswer,
    "copywriting should answer in chat rather than create a file");
Assert(
    ConversationPolicy.Classify("请只在对话里回答，不要创建文件") ==
    AgentTaskRoute.DirectAnswer,
    "a negated file action must not expose file tools");
Assert(
    ConversationPolicy.Classify("请保存为 D:\\HuiTian\\UserContent\\标题.txt") ==
    AgentTaskRoute.FileWork,
    "an explicit save request should use file tools");
var attachmentRoute = ConversationPolicy.Classify(
    "总结这个附件并发给微信联系人凉雨",
    hasAttachments: true);
Assert(attachmentRoute == AgentTaskRoute.ComputerControl,
    "an explicit messaging action should take precedence over its attachment");
Assert(
    ConversationPolicy.IsToolAllowed("huitian_read_document", attachmentRoute) &&
    ConversationPolicy.IsToolAllowed("huitian_type_active", attachmentRoute),
    "attachment-to-WeChat route should allow reading and computer control");
Assert(
    ConversationPolicy.IsToolAllowed(
        "huitian_read_document",
        ConversationPolicy.Classify("根据附件制作一份PPT", hasAttachments: true)),
    "presentation route should allow source-document reading");
Assert(
    ConversationPolicy.Classify("根据附件制作一份PPT", hasAttachments: true) ==
    AgentTaskRoute.Presentation,
    "an attachment must not hide an explicit presentation request");
Assert(
    ConversationPolicy.SanitizeAssistantText(
        "思考过程：先回答旧问题。\n最终回答：\n这是当前问题的答案。") ==
    "这是当前问题的答案。",
    "unwrapped reasoning before the final marker must remain hidden");
Assert(
    ConversationPolicy.SanitizeAssistantText(
        "<think>answer the previous question</think>\n最终回答：当前答案") ==
    "当前答案",
    "tagged reasoning must remain hidden");
Assert(
    !ConversationPolicy.TryExtractExplicitPreference(
        "帮我给文案想标题：以后选工作，多带走一样东西。",
        out _),
    "quoted copy must not become a user preference");
Assert(
    ConversationPolicy.TryExtractExplicitPreference(
        "以后回答文案时先给我三个标题",
        out _),
    "an explicit future preference should be retained");

var aggregateSummary = ConversationPolicy.BuildConversationSummary(
[
    new("user", "帮我做一页PPT"),
    new("assistant", "演示文稿已生成。"),
    new("user", "再给这段文案起一个标题"),
    new("assistant", "标题：真正能带走的，从来不是工牌。"),
]);
Assert(
    aggregateSummary.Contains("先处理", StringComparison.Ordinal) &&
    aggregateSummary.Contains("随后处理", StringComparison.Ordinal) &&
    aggregateSummary.Contains("PPT", StringComparison.OrdinalIgnoreCase) &&
    aggregateSummary.Contains("文案", StringComparison.Ordinal),
    "conversation summary should cover multiple requests");
var attachmentContext = ConversationPolicy.BuildResumeContext(
[
    new(
        "user",
        "看看这张图",
        [@"D:\HuiTian\UserContent\图片\参考.png"]),
]);
Assert(
    attachmentContext.Contains(@"D:\HuiTian\UserContent\图片\参考.png",
        StringComparison.OrdinalIgnoreCase) &&
    ConversationPolicy.ReferencesPriorAttachments("把刚才那张图发给微信联系人"),
    "bounded follow-up context should retain an explicitly referenced attachment path");

var answerContract = ConversationPolicy.BuildTurnContract(
    "帮我写一句文案",
    AgentTaskRoute.DirectAnswer);
var toolFilteringPayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "developer",
      "content": [{ "type": "input_text", "text": "产品身份。\n<permissions instructions>\nA very long generic sandbox primer.\n</permissions instructions>" }]
    },
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(answerContract)!.ToJsonString()}} }]
    }
  ],
  "tools": [
    { "type": "function", "name": "huitian_write_text_file" },
    { "type": "function", "name": "huitian_capture_screen" },
    { "type": "function", "name": "shell" },
    { "type": "custom", "name": "local_shell" }
  ]
}
""";
var directAnswerRequest = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(toolFilteringPayload)))!.AsObject();
Assert(
    directAnswerRequest["tools"]!.AsArray().Count == 0,
    "direct answers must not expose product or native tools to the model");
Assert(
    directAnswerRequest["thinking_budget_tokens"]!.GetValue<int>() ==
    LocalResponsesBridge.DirectAnswerThinkingBudgetTokens,
    "direct answers should use the short per-request thinking budget");
Assert(
    directAnswerRequest["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false,
    "direct answers should disable hidden thinking in the local chat template");
var normalizedDirectDeveloper = directAnswerRequest["input"]!.AsArray()
    .OfType<JsonObject>()
    .Single(item => item["role"]?.GetValue<string>() == "developer");
var normalizedDirectDeveloperText = string.Join(
    "\n",
    normalizedDirectDeveloper["content"]!.AsArray()
        .OfType<JsonObject>()
        .Select(item => item["text"]?.GetValue<string>() ?? string.Empty));
Assert(
    normalizedDirectDeveloperText.Contains("产品身份", StringComparison.Ordinal) &&
    !normalizedDirectDeveloperText.Contains("permissions instructions", StringComparison.Ordinal) &&
    !normalizedDirectDeveloperText.Contains("sandbox primer", StringComparison.Ordinal),
    "direct answers should omit irrelevant permission guidance without losing product identity");

var protectedRoutePayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "developer",
      "content": [{ "type": "input_text", "text": "<permissions instructions>protected route guidance</permissions instructions>" }]
    },
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(ConversationPolicy.BuildTurnContract(
          "保存到 D:\\HuiTian\\UserContent\\结果.txt",
          AgentTaskRoute.FileWork))!.ToJsonString()}} }]
    }
  ],
  "tools": []
}
""";
var protectedRouteRequest = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(protectedRoutePayload)))!.AsObject();
Assert(
    !protectedRouteRequest.ToJsonString().Contains(
        "protected route guidance",
        StringComparison.Ordinal),
    "tool-capable routes should keep real App Server enforcement but omit its repeated prose from the local-model prefill");
Assert(
    protectedRouteRequest["thinking_budget_tokens"]!.GetValue<int>() ==
    LocalResponsesBridge.TaskThinkingBudgetTokens,
    "tool-capable routes must retain the full task thinking budget");
Assert(
    protectedRouteRequest["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false,
    "declared product routes should disable hidden template thinking and rely on bounded task contracts");

var compositeContract = ConversationPolicy.BuildTurnContract(
    "给我查下未来大理的天气，出个旅行攻略，做成ppt，发给微信里的凉雨工作号",
    AgentTaskRoute.Presentation);
var compositePayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(compositeContract)!.ToJsonString()}} }]
    }
  ],
  "tools": [
    { "type": "function", "name": "huitian_search_web" },
    { "type": "function", "name": "huitian_create_presentation" },
    { "type": "function", "name": "huitian_send_chat_files" },
    { "type": "function", "name": "huitian_move_file" }
  ]
}
""";
var normalizedComposite = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(compositePayload)))!.AsObject();
Assert(
    normalizedComposite["thinking_budget_tokens"]!.GetValue<int>() ==
    LocalResponsesBridge.TaskThinkingBudgetTokens &&
    normalizedComposite["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false,
    "natural-language wording must not change the declared route's reasoning budget");
var normalizedCompositeTools = normalizedComposite["tools"]!.AsArray()
    .Select(item => item?["name"]?.GetValue<string>())
    .Where(name => name is not null)
    .ToArray();
Assert(
    normalizedCompositeTools.Contains("huitian_search_web", StringComparer.Ordinal) &&
    normalizedCompositeTools.Contains("huitian_create_presentation", StringComparer.Ordinal) &&
    normalizedCompositeTools.Contains("huitian_send_chat_files", StringComparer.Ordinal) &&
    !normalizedCompositeTools.Contains("huitian_move_file", StringComparer.Ordinal),
    "the presentation route must retain only tools that belong to this workflow");

var generalCompositeContract = ConversationPolicy.BuildTurnContract(
    "查最新资料，整理成文件，再发给微信联系人",
    AgentTaskRoute.Composite);
var generalCompositePayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(generalCompositeContract)!.ToJsonString()}} }]
    }
  ],
  "tools": [
    { "type": "function", "name": "huitian_search_web" },
    { "type": "function", "name": "huitian_write_text_file" },
    { "type": "function", "name": "huitian_send_chat_files" }
  ]
}
""";
var normalizedGeneralComposite = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(generalCompositePayload)))!.AsObject();
Assert(
    normalizedGeneralComposite["thinking_budget_tokens"]!.GetValue<int>() ==
    LocalResponsesBridge.CompositeThinkingBudgetTokens &&
    normalizedGeneralComposite["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false &&
    normalizedGeneralComposite["tools"]!.AsArray().Count == 3,
    "general mixed tasks must retain every requested capability while using a short planning budget");

var lifecycleContract = ConversationPolicy.BuildUniversalTurnContract(
    "opaque unseen wording");
var lifecycleFilteringPayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(lifecycleContract)!.ToJsonString()}} }]
    }
  ],
  "tools": [
    { "type": "function", "name": "huitian_begin_task" },
    { "type": "function", "name": "huitian_execute" },
    { "type": "function", "name": "huitian_write_text_file" },
    { "type": "function", "name": "huitian_finish_task" }
  ]
}
""";
var normalizedLifecycleRequest = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(lifecycleFilteringPayload)))!.AsObject();
var normalizedLifecycleTools = normalizedLifecycleRequest["tools"]!.AsArray()
    .Select(item => item?["name"]?.GetValue<string>())
    .Where(name => name is not null)
    .ToArray();
Assert(
    normalizedLifecycleTools.SequenceEqual(["huitian_execute"]),
    "the universal local-model turn must see one compact structured executor instead of lifecycle and concrete schema repetition");
Assert(
    normalizedLifecycleRequest["tool_choice"]?["type"]?.GetValue<string>() == "function" &&
    normalizedLifecycleRequest["tool_choice"]?["name"]?.GetValue<string>() == "huitian_execute" &&
    normalizedLifecycleRequest["parallel_tool_calls"]?.GetValue<bool>() == false,
    "the initial universal Composite turn must immediately choose the compact semantic executor");

var lifecycleFollowUpPayload = $$"""
{
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [{ "type": "input_text", "text": {{JsonValue.Create(lifecycleContract)!.ToJsonString()}} }]
    },
    {
      "type": "function_call_output",
      "call_id": "call-universal-1",
      "output": "the requested file now exists and has been verified"
    }
  ],
  "tools": [
    { "type": "function", "name": "huitian_execute" }
  ]
}
""";
var normalizedLifecycleFollowUp = JsonNode.Parse(LocalResponsesBridge.NormalizeResponsesRequest(
    Encoding.UTF8.GetBytes(lifecycleFollowUpPayload)))!.AsObject();
Assert(
    normalizedLifecycleFollowUp["tool_choice"] is null &&
    normalizedLifecycleFollowUp["parallel_tool_calls"]?.GetValue<bool>() == false,
    "a universal follow-up containing function_call_output must remain automatic instead of forcing the same tool again");

var resolvedUniversalWrite = CodexAppServerClient.ResolveUniversalToolCall(
    JsonSerializer.SerializeToElement(new
    {
        capability = "write_text_file",
        arguments = new { path = @"D:\HuiTian\UserContent\compact.txt", content = "ok" },
    }));
Assert(
    resolvedUniversalWrite.ToolName == "huitian_write_text_file" &&
    resolvedUniversalWrite.Arguments.GetProperty("content").GetString() == "ok",
    "the compact semantic executor must resolve to the existing concrete adapter without reparsing user language");
var resolvedShortEnvelope = CodexAppServerClient.ResolveUniversalToolCall(
    JsonSerializer.SerializeToElement(new
    {
        c = "create_spreadsheet",
        a = new { title = "月度数据", headers = new[] { "项目", "金额" }, rows = Array.Empty<string[]>() },
        d = new[] { "sheet" },
    }));
var parsedShortDeliverables = CodexAppServerClient.ParseDeliverables(
    JsonSerializer.SerializeToElement(new
    {
        c = "send_chat_files",
        a = new { platform = "微信", contact = "同事", filePaths = new[] { @"D:\HuiTian\UserContent\result.xlsx" } },
        d = new[] { "sheet", "sent" },
    }));
Assert(
    resolvedShortEnvelope.ToolName == "huitian_create_spreadsheet" &&
    resolvedShortEnvelope.Arguments.GetProperty("title").GetString() == "月度数据" &&
    parsedShortDeliverables.Select(item => item.Kind).SequenceEqual(
        [IntentDeliverableKind.Spreadsheet, IntentDeliverableKind.ExternalDelivery]),
    "new universal turns must use the short c/a/d envelope while retaining the full final-deliverable contract");
var enrichedShortDeliverables = CodexAppServerClient.EnrichDeliverablesForTool(
    parsedShortDeliverables,
    "huitian_send_chat_files",
    JsonSerializer.SerializeToElement(new
    {
        platform = "微信",
        contact = "未见联系人甲",
        filePaths = new[] { @"D:\HuiTian\UserContent\result.xlsx" },
    }));
Assert(
    enrichedShortDeliverables[1].Destination == "微信/未见联系人甲" &&
    enrichedShortDeliverables[1].Description.Contains("未见联系人甲", StringComparison.Ordinal),
    "a compact sent deliverable must bind to the concrete platform and recipient selected by the send adapter");
const string groundedMarker = "ORDER_TOKEN_4f60a9ce3d744529a810";
const string groundedPath = @"D:\HuiTian\UserContent\literal\ORDER_TOKEN_4f60a9ce3d744529a810.txt";
var groundedArguments = CodexAppServerClient.GroundUniversalToolArguments(
    "huitian_write_text_file",
    JsonSerializer.SerializeToElement(new
    {
        path = @"D:\HuiTian\UserContent\literal\ORDER_TOKEN_4f60a9ce3d744529a811.txt",
        content = "ORDER_TOKEN_4f60a9ce3d744529a811",
    }),
    $"请在 {groundedPath} 写入 {groundedMarker}，唯一值仍为 {groundedMarker}。");
Assert(
    groundedArguments.GetProperty("path").GetString() == groundedPath &&
    groundedArguments.GetProperty("content").GetString() == groundedMarker,
    "unambiguous paths and repeated opaque identifiers must be grounded to the exact user literal before execution");
AssertThrows<ArgumentException>(
    () => CodexAppServerClient.ResolveUniversalToolCall(
        JsonSerializer.SerializeToElement(new
        {
            capability = "invented_action",
            arguments = new { },
        })),
    "unknown compact capabilities must be rejected before execution");

var parsedSemanticAction = SemanticIntentRouter.ParseDecision("O|chat|96", 83);
var parsedSemanticMixed = SemanticIntentRouter.ParseDecision("结果：M｜office｜88", 105);
var parsedCompactSemantic = SemanticIntentRouter.ParseDecision(
    "O",
    91,
    "让微信里的小王知道明天九点开会");
var recoveredSocialSemantic = SemanticIntentRouter.ParseDecision(
    "{\"d\":\"S\",\"comm\":{\"a\":\"discover\",\"p\":\"soul\",\"r\":\"unknown\",\"n\":true,\"o\":\"conversation\",\"m\":0}}",
    92);
Assert(
    parsedSemanticAction is
    {
        Mode: SemanticIntentMode.Operate,
        Domain: SemanticIntentDomain.Chat,
        IsConfident: true,
        ElapsedMilliseconds: 83,
    } &&
    parsedSemanticMixed is
    {
        Mode: SemanticIntentMode.Mixed,
        Domain: SemanticIntentDomain.Office,
        IsConfident: true,
    } &&
    parsedCompactSemantic is
    {
        Mode: SemanticIntentMode.Operate,
        Domain: SemanticIntentDomain.Other,
        IsConfident: true,
    } &&
    SemanticIntentRouter.ParseDecision("W", 0, "任何无关的用户文字") is
    { Mode: SemanticIntentMode.Operate, Domain: SemanticIntentDomain.Chat } &&
    SemanticIntentRouter.ParseDecision("我认为应该操作电脑") is null,
    "the semantic router must parse model schema only, never infer missing domains from user keywords");
Assert(recoveredSocialSemantic is
    {
        Mode: SemanticIntentMode.Operate,
        Domain: SemanticIntentDomain.Chat,
        CommunicationTargets: { FindNewPeople: true },
    }, "validated communication slots should recover a contradictory one-letter system route without matching user text");
Assert(
    CodexAppServerClient.CanRunToolInParallel("huitian_search_web") &&
    CodexAppServerClient.CanRunToolInParallel("huitian_read_document") &&
    !CodexAppServerClient.CanRunToolInParallel("huitian_click_point") &&
    !CodexAppServerClient.CanRunToolInParallel("huitian_send_chat_message"),
    "independent reads may run concurrently while UI and delivery side effects remain serialized");

var extractedSearch = WindowsToolBridge.ExtractSearchResults(
    "<li class=\"b_algo\"><h2><a href=\"https://example.cn/weather\">大理天气</a></h2><p>未来一周昼夜温差较大。</p></li>");
Assert(
    extractedSearch.Count == 1 &&
    extractedSearch[0].Contains("未来一周昼夜温差较大", StringComparison.Ordinal),
    "direct public search extraction must return readable summaries without a screenshot");

var toolFactory = typeof(CodexAppServerClient).GetMethod(
    "CreateDynamicToolDefinitions",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
    ?? throw new MissingMethodException("route-specific tool factory is missing");
var directThreadTools = (object[]?)toolFactory.Invoke(
    null,
    [AgentTaskRoute.DirectAnswer]);
Assert(
    directThreadTools is { Length: 0 },
    "a direct-answer thread must register no computer tools; each later user turn creates a fresh route-specific thread");
var computerThreadTools = (object[]?)toolFactory.Invoke(
    null,
    [attachmentRoute]);
var computerToolNames = (computerThreadTools ?? [])
    .Select(tool => tool.GetType().GetProperty("name")?.GetValue(tool) as string)
    .Where(name => name is not null)
    .ToArray();
Assert(
    computerToolNames.Contains("huitian_read_document", StringComparer.Ordinal) &&
    computerToolNames.Contains("huitian_type_active", StringComparer.Ordinal) &&
    computerToolNames.Contains("huitian_send_chat_message", StringComparer.Ordinal),
    "an attachment-to-WeChat App Server thread must retain reading and the dedicated messaging tool");

var instructionFactory = typeof(CodexAppServerClient).GetMethod(
    "CreateBaseInstructions",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
    ?? throw new MissingMethodException("route-specific instruction factory is missing");
var directInstructions = instructionFactory.Invoke(
    null,
    [AgentTaskRoute.DirectAnswer]) as string;
Assert(
    directInstructions is { Length: < 900 } &&
    directInstructions.Contains("回天AI", StringComparison.Ordinal) &&
    directInstructions.Contains("当前轮次唯一要求", StringComparison.Ordinal) &&
    directInstructions.Contains("情绪", StringComparison.Ordinal) &&
    directInstructions.Contains("computer", StringComparison.Ordinal),
    "direct-answer base instructions must preserve identity, pragmatic context, and safe mode switching");

Assert(
    CodexAppServerClient.SelectReasoningEffort(
        "只回答图片最上方的英文标题。",
        hasVisualContext: true) == "low",
    "visual turns should avoid a second long hidden-thinking pass before acting");
Assert(
    CodexAppServerClient.SelectReasoningEffort(
        ConversationPolicy.BuildTurnContract(
            "请制作一份完整的年度经营演示文稿",
            AgentTaskRoute.Presentation),
        hasVisualContext: false) == "low",
    "presentation work should rely on its task contract rather than a long hidden-thinking preamble");
Assert(
    CodexAppServerClient.SelectReasoningEffort(
        compositeContract,
        hasVisualContext: false) == "low",
    "a fixed composite workflow should begin at low effort instead of waiting for xhigh planning");
Assert(
    CodexAppServerClient.NormalizeReasoningEffort("high") == "xhigh" &&
    CodexAppServerClient.NormalizeReasoningEffort("unexpected") == "medium",
    "the App Server boundary must never forward an unsupported reasoning effort");
Assert(
    CodexAppServerClient.IsNonRecoverableTurnError(
        "Jinja Exception: Unexpected reasoning effort high. Supported types are xhigh, medium, and low") &&
    CodexAppServerClient.IsNonRecoverableTurnError("failed to decode image") &&
    !CodexAppServerClient.IsNonRecoverableTurnError(
        "We're currently experiencing high demand"),
    "deterministic configuration/image errors must not enter generic automatic recovery");

Assert(
    !SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
        "给微信联系人发送你好",
        ["切换窗口已完成", "输入文字已完成", "使用快捷键已完成"],
        out _),
    "a UI path without post-action observation must not be learned");
Assert(
    !SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
        "给微信联系人发送你好",
        [
            "点击控件已完成：已点击：凉雨（工作号）", "输入文字已完成",
            "观察屏幕已完成",
        ],
        out _),
    "clicking a contact must not be mistaken for clicking Send");
Assert(
    SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
        "给微信联系人发送你好",
        [
            "观察窗口已完成", "切换窗口已完成", "观察屏幕已完成", "定位点击已完成",
            "输入文字已完成", "观察屏幕已完成",
            "使用快捷键已完成：已在微信中按下 ENTER。", "观察屏幕已完成",
        ],
        out var verificationEvidence) &&
    !string.IsNullOrWhiteSpace(verificationEvidence),
    "a fully observed messaging path should be reusable");
Assert(
    SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
        "给微信联系人发送你好",
        ["发送消息已完成：已核验聊天记录中的新消息"],
        out _),
    "a verified dedicated messaging result should be reusable without replaying low-level steps");
Assert(
    SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
        "给QQ联系人发送你好",
        [
            "切换窗口已完成", "输入文字已完成",
            "使用快捷键已完成：已在QQ中按下 ENTER。", "观察屏幕已完成",
        ],
        out _),
    "QQ messaging must use the same post-send verification rule without being classified as WeChat");

Assert(
    WindowsUiAutomation.LevenshteinDistance("凉雨", "良宇") == 2 &&
    WindowsUiAutomation.LevenshteinDistance("张伟", "张威") == 1,
    "contact candidate distance should support typo and homophone fallback suggestions");

var attemptTracker = new AutomationAttemptTracker(TimeSpan.FromMinutes(2));
Assert(!attemptTracker.ShouldBlock("click:contact"), "a new strategy must be allowed");
Assert(attemptTracker.RecordFailure("click:contact") == 1 &&
       !attemptTracker.ShouldBlock("click:contact"),
    "one failed attempt must remain retryable after observing state");
Assert(attemptTracker.RecordFailure("click:contact") == 2 &&
       attemptTracker.ShouldBlock("click:contact"),
    "the same ineffective strategy must be blocked after two attempts");
attemptTracker.RecordSuccess("click:contact");
Assert(!attemptTracker.ShouldBlock("click:contact"),
    "verified success must clear the bounded retry guard");

using var orderedArguments = JsonDocument.Parse(
    """{"windowTitle":"微信","target":{"name":"凉雨","options":{"exact":true,"limit":3}},"keys":["CTRL","ENTER"]}""");
using var reorderedArguments = JsonDocument.Parse(
    """{"keys":["CTRL","ENTER"],"target":{"options":{"limit":3,"exact":true},"name":"凉雨"},"windowTitle":"微信"}""");
using var reversedArrayArguments = JsonDocument.Parse(
    """{"keys":["ENTER","CTRL"],"target":{"name":"凉雨","options":{"exact":true,"limit":3}},"windowTitle":"微信"}""");
var orderedAttemptKey = WindowsToolBridge.BuildAttemptKey(
    "huitian_press_keys",
    orderedArguments.RootElement);
var reorderedAttemptKey = WindowsToolBridge.BuildAttemptKey(
    "huitian_press_keys",
    reorderedArguments.RootElement);
Assert(
    orderedAttemptKey == reorderedAttemptKey,
    "recursive object-property order must not create a different automation strategy");
Assert(
    orderedAttemptKey != WindowsToolBridge.BuildAttemptKey(
        "huitian_press_keys",
        reversedArrayArguments.RootElement),
    "array order must remain part of the automation strategy semantics");
var canonicalAttemptTracker = new AutomationAttemptTracker(TimeSpan.FromMinutes(2));
Assert(
    canonicalAttemptTracker.RecordFailure(orderedAttemptKey) == 1 &&
    canonicalAttemptTracker.RecordFailure(reorderedAttemptKey) == 2 &&
    canonicalAttemptTracker.ShouldBlock(orderedAttemptKey),
    "the retry guard must count reordered JSON arguments as the same failed path");

Assert(
    SelfEvolutionEngine.Classify("给QQ联系人凉雨发消息") == "QQ" &&
    SelfEvolutionEngine.Classify("给微信联系人凉雨发消息") == "微信" &&
    SelfEvolutionEngine.Classify("给联系人发消息") == "即时通讯",
    "QQ, WeChat, and platform-unspecified messaging must have separate categories");
var platformLearnings = new[]
{
    new TaskLearning
    {
        Trigger = "给微信联系人凉雨发消息",
        Strategy = "激活微信并发送",
        Category = "微信",
        Succeeded = true,
        VerifiedSuccess = true,
        SuccessCount = 2,
    },
    new TaskLearning
    {
        Trigger = "给QQ联系人凉雨发消息",
        Strategy = "激活QQ并发送",
        Category = "微信", // Simulates state saved by the old misclassification bug.
        Succeeded = true,
        VerifiedSuccess = true,
        SuccessCount = 2,
    },
};
var qqRelevant = SelfEvolutionEngine.FindRelevant(
        platformLearnings,
        "在QQ里给联系人凉雨发消息",
        5)
    .ToArray();
Assert(
    qqRelevant.Length == 1 &&
    qqRelevant[0].Trigger.Contains("QQ", StringComparison.OrdinalIgnoreCase) &&
    qqRelevant[0].Category == "QQ",
    "QQ task recall must repair stale categories and never reuse a WeChat-only route");
var crossPlatformState = new ProductState
{
    TaskLearnings =
    [
        new TaskLearning
        {
            Trigger = "给微信联系人凉雨发消息",
            Strategy = "激活聊天窗口 → 输入并发送",
            Category = "微信",
            Succeeded = true,
            VerifiedSuccess = true,
            SuccessCount = 1,
        },
        new TaskLearning
        {
            Trigger = "给QQ联系人凉雨发消息",
            Strategy = "激活聊天窗口 → 输入并发送",
            Category = "微信",
            Succeeded = true,
            VerifiedSuccess = true,
            SuccessCount = 1,
        },
    ],
};
SelfEvolutionEngine.Run(crossPlatformState);
Assert(
    crossPlatformState.TaskLearnings.Count == 2 &&
    crossPlatformState.TaskLearnings.Select(item => item.Category)
        .OrderBy(value => value, StringComparer.Ordinal)
        .SequenceEqual(["QQ", "微信"], StringComparer.Ordinal),
    "self-evolution must not merge identical QQ and WeChat strategies across platforms");

var migrationRoot = Path.Combine(
    @"D:\HuiTian\Temp",
    "ProtocolStateMigration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(migrationRoot);
try
{
    await File.WriteAllTextAsync(
        Path.Combine(migrationRoot, "product-state.json"),
        """
        {
          "theme": "Light",
          "learningSchemaVersion": 0,
          "artifactDiscoveryVersion": 3,
          "userPreferences": [
            "帮我给文案想标题：以后选工作，多带走一样东西。"
          ],
          "taskLearnings": [
            {
              "trigger": "给微信联系人发消息",
              "strategy": "搜索文件已完成",
              "succeeded": true,
              "successCount": 1,
              "category": "微信"
            }
          ]
        }
        """,
        Encoding.UTF8);
    var migrated = new ProductStateStore(migrationRoot).Load();
    Assert(migrated.LearningSchemaVersion == 2, "legacy learning state should migrate once");
    Assert(migrated.Theme == "Dark" && migrated.VisualThemeVersion == 1,
        "the old light default should migrate to the personal dark theme once");
    Assert(migrated.UserPreferences.Count == 0, "quoted copy preference should be removed");
    Assert(migrated.TaskLearnings.Count == 0, "unverified legacy routes should be removed");
    migrated.Theme = "Light";
    await new ProductStateStore(migrationRoot).SaveAsync(migrated);
    Assert(new ProductStateStore(migrationRoot).Load().Theme == "Light",
        "a later deliberate light theme choice should remain intact");
}
finally
{
    Directory.Delete(migrationRoot, recursive: true);
}

var structuredIntentJson = """
    ```json
    {
      "task_type": "multi_app_task",
      "domain": "multi_app",
      "goal": "整理现有资料并交付给指定接收人",
      "requires_computer": true,
      "actions": ["read", "transform", "send"],
      "applications": ["Document", "Communication"],
      "entities": [
        {"role":"file","type":"document","value":"current_document","confidence":0.91},
        {"role":"recipient","type":"person","value":"current_recipient","confidence":0.88}
      ],
      "constraints": ["preserve formatting"],
      "success_conditions": [
        {"kind":"artifact","description":"修改后的文件存在"},
        {"kind":"external_state","description":"正确文件出现在正确会话中"}
      ],
      "plan": [
        {"id":"inspect","title":"读取现有资料","action":"read","app":"Document","target":"current_document","depends_on":[],"success_conditions":[{"kind":"return_value","description":"内容已读取"}],"risk":"none","estimated_latency_ms":200},
        {"id":"deliver","title":"交付完成结果","action":"send","app":"Communication","target":"current_recipient","depends_on":["inspect"],"success_conditions":[{"kind":"external_state","description":"正确内容已出现"}],"risk":"external_effect","estimated_latency_ms":500}
      ],
      "risk": "external_effect",
      "clarification": {"required":false,"reason":null,"question":null},
      "confidence": 0.93
    }
    ```
    """;
Assert(
    IntentUnderstandingEngine.TryParseResolution(
        structuredIntentJson,
        37,
        "test-model",
        out var structuredIntent) &&
    structuredIntent.TaskType == SemanticTaskType.MultiAppTask &&
    structuredIntent.Domain == IntentExecutionDomain.MultiApp &&
    structuredIntent.RequiresComputer &&
    structuredIntent.Actions.SequenceEqual(["read", "transform", "send"]) &&
    structuredIntent.Applications.Count == 2 &&
    structuredIntent.Entities.Count == 2 &&
    structuredIntent.SuccessConditions.Count == 2 &&
    structuredIntent.Plan.Count == 2 &&
    structuredIntent.Plan[1].DependsOn.SequenceEqual(["inspect"]) &&
    structuredIntent.Risk == IntentRiskLevel.ExternalEffect &&
    structuredIntent.ToAgentRoute() == AgentTaskRoute.Composite,
    "structured semantic output should parse without inspecting the original user wording");
Assert(
    !IntentUnderstandingEngine.TryParseResolution(
        "{\"task_type\":\"computer_task\",\"domain\":\"file\"}",
        1,
        "test-model",
        out _),
    "semantic output without a resolved goal must be rejected");
var semanticFallback = IntentUnderstandingEngine.BuildFallback(
    "opaque request that the fallback must not reinterpret",
    AgentTaskRoute.FileWork,
    hasAttachments: false,
    elapsedMilliseconds: 12);
Assert(
    semanticFallback.Goal == "opaque request that the fallback must not reinterpret" &&
    semanticFallback.Domain == IntentExecutionDomain.File &&
    semanticFallback.RequiresComputer &&
    semanticFallback.Source == "deterministic-fallback",
    "fallback routing may preserve availability but must not parse or rewrite user language");

var performanceRoot = Path.Combine(
    @"D:\HuiTian\Temp",
    "PerformanceProfile-" + Guid.NewGuid().ToString("N"));
try
{
    var performancePaths = new HuiTianPaths(performanceRoot);
    performancePaths.EnsureDirectories();
    var profiler = new TaskPerformanceProfiler(performancePaths);
    var firstSession = profiler.StartSession("profile-success");
    firstSession.SetIntent(structuredIntent);
    firstSession.RecordPhase("planning", 14);
    firstSession.MarkFirstAction();
    firstSession.RecordTool("structured-adapter", 21, succeeded: true);
    await firstSession.CompleteAsync(succeeded: true);
    var secondSession = profiler.StartSession("profile-recovery");
    secondSession.SetIntent(semanticFallback);
    secondSession.RecordTool("native-adapter", 12, succeeded: false);
    secondSession.RecordTool(
        "visual-fallback",
        34,
        succeeded: true,
        usedVision: true,
        wasFallback: true);
    await secondSession.CompleteAsync(succeeded: true);
    var performanceSummary = await profiler.ReadSummaryAsync();
    Assert(
        performanceSummary.Samples == 2 &&
        performanceSummary.SuccessRate == 1d &&
        performanceSummary.P50TotalMilliseconds >= 0 &&
        performanceSummary.P95TotalMilliseconds >= performanceSummary.P50TotalMilliseconds &&
        performanceSummary.AverageVisionCalls == 0.5 &&
        performanceSummary.AverageFallbacks >= 1d,
        "performance profiler must persist first action, P50/P95, vision, fallback and outcome metrics");
}
finally
{
    if (Directory.Exists(performanceRoot))
    {
        Directory.Delete(performanceRoot, recursive: true);
    }
}

var bootstrapRoot = Path.Combine(
    @"D:\HuiTian\Temp",
    "ContextBootstrap-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(bootstrapRoot);
try
{
    var attached = Path.Combine(bootstrapRoot, "unseen-input.txt");
    await File.WriteAllTextAsync(attached, "bootstrap test", Encoding.UTF8);
    var bootstrap = await new TaskContextBootstrapper().CaptureAsync(
        [attached],
        [],
        Environment.ProcessId);
    var bootstrapText = bootstrap.FormatForAgent();
    Assert(
        bootstrap.ElapsedMilliseconds < 2_000 &&
        bootstrap.Files.Count == 1 &&
        bootstrap.Files[0].Path == attached &&
        bootstrapText.Contains("不是任务完成证据", StringComparison.Ordinal) &&
        !bootstrapText.Contains("截图", StringComparison.Ordinal),
        "task bootstrap must return real read-only file/window context quickly without pretending it completed the goal");
}
finally
{
    Directory.Delete(bootstrapRoot, recursive: true);
}

var registryRoot = Path.Combine(
    @"D:\HuiTian\Temp",
    "ApplicationRegistry-" + Guid.NewGuid().ToString("N"));
try
{
    var registryPaths = new HuiTianPaths(registryRoot);
    registryPaths.EnsureDirectories();
    var fakeShortcut = Path.Combine(registryPaths.Temp, "Acme Workspace.lnk");
    await File.WriteAllTextAsync(fakeShortcut, "test shortcut placeholder");
    var appRegistry = new WindowsApplicationRegistry(registryPaths);
    appRegistry.RegisterOrUpdate(new WindowsApplicationRecord(
        "test:acme-workspace",
        "Acme Workspace",
        fakeShortcut,
        ApplicationLaunchKind.Shortcut,
        "Acme",
        "test",
        ["Acme", "Workspace"],
        DateTimeOffset.Now));
    await appRegistry.SaveAsync();
    Assert(
        appRegistry.TryResolve("Acme Workspace", out var exactApp) &&
        exactApp.LaunchTarget == fakeShortcut &&
        appRegistry.TryResolve("Acme", out var aliasApp) &&
        aliasApp.Id == exactApp.Id,
        "application registry must resolve semantic app names and aliases without a live folder scan");
    var reloadedRegistry = new WindowsApplicationRegistry(registryPaths);
    Assert(
        reloadedRegistry.TryResolve("Workspace", out var reloadedApp) &&
        reloadedApp.LaunchTarget == fakeShortcut,
        "application registry must persist its launch index under the configured D-drive root");
}
finally
{
    if (Directory.Exists(registryRoot))
    {
        Directory.Delete(registryRoot, recursive: true);
    }
}

var executionMemoryRoot = Path.Combine(
    @"D:\HuiTian\Temp",
    "ExecutionMemory-" + Guid.NewGuid().ToString("N"));
try
{
    var memoryPaths = new HuiTianPaths(executionMemoryRoot);
    memoryPaths.EnsureDirectories();
    var memory = new ExecutionMemoryStore(memoryPaths);
    memory.Record(
        "Spreadsheet",
        "export",
        ComputerTaskChannel.AppAdapter,
        "office-export-adapter",
        succeeded: true,
        verified: true,
        elapsedMilliseconds: 90,
        verificationKind: "artifact");
    memory.Record(
        "Spreadsheet",
        "export",
        ComputerTaskChannel.AppAdapter,
        "office-export-adapter",
        succeeded: true,
        verified: true,
        elapsedMilliseconds: 70,
        verificationKind: "artifact");
    memory.Record(
        "Spreadsheet",
        "export",
        ComputerTaskChannel.Visual,
        "visual-export",
        succeeded: false,
        verified: false,
        elapsedMilliseconds: 500,
        verificationKind: "none");
    var memoryTask = new ComputerTaskContract(
        ComputerTaskMode.Operate,
        "Export active data",
        "export",
        app: "Spreadsheet",
        completionEvidence:
        [
            new CompletionEvidenceRequirement(
                CompletionEvidenceKind.Artifact,
                "Exported artifact exists"),
        ]);
    var recommended = memory.Find(memoryTask);
    Assert(
        recommended.Count == 2 &&
        recommended[0].StrategyId == "office-export-adapter" &&
        recommended[0].IsTrusted &&
        recommended[0].AverageDurationMilliseconds == 80 &&
        recommended[1].Failures == 1,
        "execution memory must rank verified structured paths without storing a user phrase");
    await memory.SaveAsync();
    var reloadedMemory = new ExecutionMemoryStore(memoryPaths);
    Assert(
        reloadedMemory.Find(memoryTask).First().VerifiedSuccesses == 2,
        "verified execution memory must persist under the configured D-drive root");
}
finally
{
    if (Directory.Exists(executionMemoryRoot))
    {
        Directory.Delete(executionMemoryRoot, recursive: true);
    }
}

var declaredArguments = JsonSerializer.SerializeToElement(new
{
    goal = "将当前报告发送给指定联系人",
    taskType = "multi_app_task",
    domain = "communication",
    actions = new[] { "locate", "verify", "send" },
    applications = new[] { "Communication" },
    @object = "当前报告",
    recipient = "指定联系人",
    constraints = new[] { "保持原文件不变" },
    success = new[] { "正确报告出现在正确会话中" },
    risk = "external_effect",
});
var declaredIntent = CodexAppServerClient.ParseDeclaredIntent(declaredArguments);
Assert(
    declaredIntent.TaskType == SemanticTaskType.MultiAppTask &&
    declaredIntent.Domain == IntentExecutionDomain.Communication &&
    declaredIntent.RequiresComputer &&
    declaredIntent.Actions.SequenceEqual(["locate", "verify", "send"]) &&
    declaredIntent.Entities.Any(entity => entity.Role == IntentEntityRole.Recipient) &&
    declaredIntent.Risk == IntentRiskLevel.ExternalEffect &&
    declaredIntent.SuccessConditions.Single().Kind == IntentEvidenceKind.ExternalState &&
    declaredIntent.Source == "agent-semantic-contract",
    "the in-turn semantic declaration must become a structured execution contract without reparsing user text");

var declaredPlan = declaredIntent.Plan;
Assert(
    declaredPlan.Count == 3 &&
    declaredPlan.Select(step => step.Id).SequenceEqual(["step-1", "step-2", "step-3"]) &&
    declaredPlan.Select(step => step.Action).SequenceEqual(["locate", "verify", "send"]) &&
    declaredPlan.Select(step => step.App).SequenceEqual(
        ["Communication", "Communication", "Communication"]) &&
    declaredPlan[0].DependsOn.Count == 0 &&
    declaredPlan[1].DependsOn.SequenceEqual(["step-1"]) &&
    declaredPlan[2].DependsOn.SequenceEqual(["step-2"]),
    "a declared multi-step task must preserve action order and compile to a linear dependency chain");
Assert(
    declaredPlan[0].SuccessConditions.Single().Kind == IntentEvidenceKind.StateObservation &&
    declaredPlan[1].SuccessConditions.Single().Kind == IntentEvidenceKind.StateObservation &&
    declaredPlan[2].SuccessConditions.SequenceEqual(declaredIntent.SuccessConditions) &&
    declaredPlan.Take(2).All(step => step.Risk == IntentRiskLevel.Reversible) &&
    declaredPlan[2].Risk == IntentRiskLevel.ExternalEffect,
    "intermediate steps must require observed state while the final step retains the declared evidence and risk");

var declaredOfficeIntent = CodexAppServerClient.ParseDeclaredIntent(
    JsonSerializer.SerializeToElement(new
    {
        goal = "保存修改后的文档",
        taskType = "computer_task",
        domain = "office",
        actions = new[] { "edit", "save" },
        applications = new[] { "Word", "FileSystem" },
        success = new[] { "目标文档已经存在" },
        risk = "reversible",
    }));
Assert(
    declaredOfficeIntent.SuccessConditions.Single().Kind == IntentEvidenceKind.Artifact &&
    declaredOfficeIntent.Plan.Select(step => step.App).SequenceEqual(["Word", "FileSystem"]) &&
    declaredOfficeIntent.Plan[^1].SuccessConditions.Single().Kind == IntentEvidenceKind.Artifact,
    "Office declarations must bind ordered applications and require artifact evidence at completion");

var declaredBrowserIntent = CodexAppServerClient.ParseDeclaredIntent(
    JsonSerializer.SerializeToElement(new
    {
        goal = "打开目标页面",
        taskType = "computer_action",
        domain = "browser",
        actions = Array.Empty<string>(),
        success = Array.Empty<string>(),
        risk = "none",
    }));
Assert(
    declaredBrowserIntent.Plan is [{ Action: "complete_goal" }] &&
    declaredBrowserIntent.SuccessConditions.Single().Kind == IntentEvidenceKind.StateObservation &&
    !string.IsNullOrWhiteSpace(declaredBrowserIntent.SuccessConditions.Single().Description),
    "an otherwise valid sparse declaration must still compile to an executable step with observable evidence");
AssertThrows<ArgumentException>(
    () => CodexAppServerClient.ParseDeclaredIntent(
        JsonSerializer.SerializeToElement(new { goal = "   " })),
    "a begin-task declaration with no semantic goal must be rejected");

var implicitContractClient = new CodexAppServerClient(
    new HuiTianPaths(@"D:\HuiTian\Cache\Tests\ImplicitToolContract"));
IntentResolution? implicitIntent = null;
AgentTaskOutcome? implicitOutcome = null;
implicitContractClient.IntentDeclared += (_, args) => implicitIntent = args.Intent;
implicitContractClient.TaskFinished += (_, args) => implicitOutcome = args.Outcome;
var privateFlags = System.Reflection.BindingFlags.NonPublic |
                   System.Reflection.BindingFlags.Instance;
var resetSemanticContract = typeof(CodexAppServerClient).GetMethod(
    "ResetSemanticContract", privateFlags)
    ?? throw new MissingMethodException(nameof(CodexAppServerClient), "ResetSemanticContract");
var ensureSemanticContract = typeof(CodexAppServerClient).GetMethod(
    "EnsureSemanticContractForTool", privateFlags)
    ?? throw new MissingMethodException(nameof(CodexAppServerClient), "EnsureSemanticContractForTool");
var recordToolEvidence = typeof(CodexAppServerClient).GetMethod(
    "RecordToolEvidence", privateFlags)
    ?? throw new MissingMethodException(nameof(CodexAppServerClient), "RecordToolEvidence");
var completeSemanticContract = typeof(CodexAppServerClient).GetMethod(
    "CompleteSemanticContractFromEvidence", privateFlags)
    ?? throw new MissingMethodException(nameof(CodexAppServerClient), "CompleteSemanticContractFromEvidence");
var implicitArtifactPath = @"D:\HuiTian\Cache\Tests\ImplicitToolContract\语义结果.txt";
Directory.CreateDirectory(Path.GetDirectoryName(implicitArtifactPath)!);
File.WriteAllText(implicitArtifactPath, "verified");
var implicitFileArguments = JsonSerializer.SerializeToElement(new
{
    path = implicitArtifactPath,
    content = "verified",
});
resetSemanticContract.Invoke(
    implicitContractClient,
    ["【当前轮次唯一要求】今日成果照此落地【本轮语义边界尚未预判】"]);
ensureSemanticContract.Invoke(
    implicitContractClient,
    ["huitian_write_text_file", implicitFileArguments, null]);
recordToolEvidence.Invoke(
    implicitContractClient,
    [
        "huitian_write_text_file",
        implicitFileArguments,
        new DynamicToolResult(
            true,
            $"文件已创建：{implicitArtifactPath}",
            ResultCode: "completed",
            ArtifactPaths: [implicitArtifactPath]),
    ]);
completeSemanticContract.Invoke(implicitContractClient, [false]);
Assert(
    implicitIntent is
    {
        Source: "agent-selected-tool-contract",
        Domain: IntentExecutionDomain.File,
        TaskType: SemanticTaskType.ComputerTask,
        RequiresComputer: true,
    } &&
    implicitIntent.Goal == "今日成果照此落地" &&
    implicitIntent.Actions.SequenceEqual(["create_text_file"]) &&
    implicitIntent.Entities.Any(entity =>
        entity.Role == IntentEntityRole.File &&
        entity.Value.EndsWith("语义结果.txt", StringComparison.Ordinal)) &&
    implicitOutcome is { Succeeded: true } &&
    implicitOutcome.Evidence.Any(item => item.Contains("文件已创建", StringComparison.Ordinal)),
    "a model-selected real tool must establish and close a semantic task contract even when the model omits begin/finish meta calls; user wording is not reparsed by keywords");
File.Delete(implicitArtifactPath);
await implicitContractClient.DisposeAsync();

var succeededOutcome = CodexAppServerClient.ParseTaskOutcome(
    JsonSerializer.SerializeToElement(new
    {
        succeeded = true,
        summary = "  报告已保存并重新读取  ",
        evidence = new[]
        {
            "  文件存在于目标路径  ",
            "重新读取后包含预期标题",
            "文件存在于目标路径",
            " ",
        },
    }));
Assert(
    succeededOutcome.Succeeded &&
    succeededOutcome.Summary == "报告已保存并重新读取" &&
    succeededOutcome.Evidence.SequenceEqual(
        ["文件存在于目标路径", "重新读取后包含预期标题"]) &&
    succeededOutcome.UnresolvedIssue is null,
    "a successful finish declaration must normalize, de-duplicate, and retain concrete evidence order");

var failedOutcome = CodexAppServerClient.ParseTaskOutcome(
    JsonSerializer.SerializeToElement(new
    {
        succeeded = false,
        summary = "目标应用要求重新登录",
        evidence = new[] { "登录页面仍然可见" },
        unresolvedIssue = " ",
    }));
Assert(
    !failedOutcome.Succeeded &&
    failedOutcome.Evidence.SequenceEqual(["登录页面仍然可见"]) &&
    failedOutcome.UnresolvedIssue == failedOutcome.Summary,
    "a failed finish declaration must retain its evidence and expose a non-empty unresolved issue");
AssertThrows<ArgumentException>(
    () => CodexAppServerClient.ParseTaskOutcome(
        JsonSerializer.SerializeToElement(new
        {
            succeeded = true,
            summary = " ",
            evidence = new[] { "窗口已核验" },
        })),
    "a finish declaration with no factual summary must be rejected");
AssertThrows<ArgumentException>(
    () => CodexAppServerClient.ParseTaskOutcome(
        JsonSerializer.SerializeToElement(new
        {
            succeeded = true,
            summary = "声称完成但没有证据",
            evidence = new object[] { " ", 7, false },
        })),
    "blank or non-text finish evidence must not satisfy the completion contract");

var universalContract = ConversationPolicy.BuildUniversalTurnContract(
    "opaque natural language that product code must not classify");
Assert(
    universalContract.Contains("系统会从本轮要求建立 goal", StringComparison.Ordinal) &&
    !universalContract.Contains("huitian_begin_task", StringComparison.Ordinal) &&
    !universalContract.Contains("huitian_finish_task", StringComparison.Ordinal) &&
    universalContract.Contains("若最终结果本来就是回答", StringComparison.Ordinal) &&
    universalContract.Contains(
        "opaque natural language that product code must not classify",
        StringComparison.Ordinal) &&
    ConversationPolicy.IsToolAllowed("huitian_begin_task", AgentTaskRoute.Composite) &&
    ConversationPolicy.IsToolAllowed("huitian_finish_task", AgentTaskRoute.Composite),
    "the universal first turn must preserve the answer-versus-action decision for the agent itself");

var universalThreadTools = (object[]?)toolFactory.Invoke(
    null,
    [AgentTaskRoute.Composite]) ?? [];
var universalToolDefinitions = universalThreadTools
    .Select(tool => JsonSerializer.SerializeToElement(tool))
    .ToArray();
var universalExecutorDefinition = universalToolDefinitions.Single(definition =>
    definition.GetProperty("name").GetString() == "huitian_execute");
var beginTaskDefinition = universalToolDefinitions.Single(definition =>
    definition.GetProperty("name").GetString() == "huitian_begin_task");
var finishTaskDefinition = universalToolDefinitions.Single(definition =>
    definition.GetProperty("name").GetString() == "huitian_finish_task");
var beginTaskSchema = beginTaskDefinition.GetProperty("inputSchema");
var finishTaskSchema = finishTaskDefinition.GetProperty("inputSchema");
var beginTaskRequired = beginTaskSchema.GetProperty("required")
    .EnumerateArray()
    .Select(item => item.GetString())
    .ToArray();
var finishTaskRequired = finishTaskSchema.GetProperty("required")
    .EnumerateArray()
    .Select(item => item.GetString())
    .ToArray();
Assert(
    universalToolDefinitions.Length == 3 &&
    universalExecutorDefinition.GetProperty("inputSchema")
        .GetProperty("required")
        .EnumerateArray()
        .Select(item => item.GetString())
        .SequenceEqual(["c", "a"]),
    "the universal thread must register one compact executor plus compatibility lifecycle contracts, not every concrete schema");
var compactDeliverables = CodexAppServerClient.ParseDeliverables(
    JsonSerializer.SerializeToElement(new
    {
        deliverables = new object[]
        {
            new { kind = "document", format = "docx" },
            new { kind = "external_delivery", destination = "目标联系人" },
        },
    }));
Assert(
    compactDeliverables.Count == 2 &&
    compactDeliverables.All(item => !string.IsNullOrWhiteSpace(item.Description)) &&
    compactDeliverables[0].Kind == IntentDeliverableKind.Document &&
    compactDeliverables[1].Kind == IntentDeliverableKind.ExternalDelivery,
    "compact deliverables must receive deterministic server-side descriptions without spending model tokens repeating prose");
Assert(
    beginTaskRequired.SequenceEqual(["goal", "taskType", "domain", "actions", "success", "risk"]) &&
    beginTaskSchema.GetProperty("properties").GetProperty("success")
        .GetProperty("minItems").GetInt32() == 1 &&
    !beginTaskSchema.GetProperty("additionalProperties").GetBoolean() &&
    beginTaskDefinition.GetProperty("description").GetString()!
        .Contains("第一项", StringComparison.Ordinal),
    "the universal begin-task tool must require a closed semantic goal, plan, risk, and success-evidence contract");
Assert(
    finishTaskRequired.SequenceEqual(["succeeded", "summary", "evidence"]) &&
    finishTaskSchema.GetProperty("properties").GetProperty("evidence")
        .GetProperty("minItems").GetInt32() == 1 &&
    !finishTaskSchema.GetProperty("additionalProperties").GetBoolean() &&
    finishTaskDefinition.GetProperty("description").GetString()!
        .Contains("结束前", StringComparison.Ordinal),
    "the universal finish-task tool must require a closed result and non-empty evidence contract");

var universalInstructions = instructionFactory.Invoke(
    null,
    [AgentTaskRoute.Composite]) as string;
Assert(
    universalInstructions is not null &&
    universalInstructions.Contains(
        "第一项响应必须是能直接推进结果的具体能力调用",
        StringComparison.Ordinal) &&
    universalInstructions.Contains("调度层", StringComparison.Ordinal) &&
    !universalInstructions.Contains("第一项必须调用 huitian_begin_task", StringComparison.Ordinal) &&
    !universalInstructions.Contains("给最终答复前必须调用 huitian_finish_task", StringComparison.Ordinal),
    "the universal execution instructions must make concrete tools immediate and leave lifecycle bookkeeping to the scheduler");

var genericOfficeArtifact = new IntentResolution(
    SemanticTaskType.ComputerTask,
    IntentExecutionDomain.Office,
    "modify an office document",
    requiresComputer: true,
    actions: ["edit_document"],
    applications: ["Word"],
    successConditions: [new IntentSuccessCondition(IntentEvidenceKind.Artifact, "document exists")],
    confidence: 0.9);
var presentationArtifact = new IntentResolution(
    SemanticTaskType.ComputerTask,
    IntentExecutionDomain.Office,
    "create a presentation",
    requiresComputer: true,
    actions: ["create_presentation"],
    applications: ["PowerPoint"],
    successConditions: [new IntentSuccessCondition(IntentEvidenceKind.Artifact, "presentation exists")],
    confidence: 0.9);
Assert(
    genericOfficeArtifact.ToAgentRoute() == AgentTaskRoute.FileWork &&
    presentationArtifact.ToAgentRoute() == AgentTaskRoute.Presentation,
    "an Office artifact must route to presentation tools only when its structured app/action declares a presentation");

var deliveryTestRoot = @"D:\HuiTian\Cache\Tests\DeliverableGate";
Directory.CreateDirectory(deliveryTestRoot);
var fakeDocxPath = Path.Combine(deliveryTestRoot, "plain-text-disguised-as-document.docx");
File.WriteAllText(fakeDocxPath, "this is not an Office package");
var documentRequirement = new IntentDeliverableRequirement(
    IntentDeliverableKind.Document,
    "一份可以继续编辑和打印的正式资料",
    "docx");
var conversationEvidence = new DeliverableExecutionEvidence(
    "huitian_respond",
    Success: true,
    RequiresVerification: false,
    ArtifactPaths: [],
    RuntimeText: "这里只显示了一段聊天文字。",
    ResultCode: "response_delivered");
var searchEvidence = new DeliverableExecutionEvidence(
    "huitian_search_web",
    Success: true,
    RequiresVerification: false,
    ArtifactPaths: [],
    RuntimeText: "检索到若干网页摘要。",
    ResultCode: "research_results");
var fakeDocumentEvidence = new DeliverableExecutionEvidence(
    "huitian_write_text_file",
    Success: true,
    RequiresVerification: false,
    ArtifactPaths: [fakeDocxPath],
    RuntimeText: "写入完成",
    ResultCode: "completed");
Assert(
    !DeliverableCompletionVerifier.Verify([], [conversationEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify([documentRequirement], [conversationEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify([documentRequirement], [searchEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify([documentRequirement], [fakeDocumentEvidence]).Succeeded,
    "no contract, chat text, intermediate research, and renamed plain text must never satisfy a document deliverable");

var documentFileName = $"通用交付门禁-{Guid.NewGuid():N}.docx";
var documentResult = await new WindowsToolBridge().ExecuteAsync(
    "huitian_create_document",
    JsonSerializer.SerializeToElement(new
    {
        title = "社区活动执行说明",
        content = "# 使用说明\n这是一份可编辑的正式资料。\n\n## 填写区域\n姓名：________________\n联系电话：________________\n[分页]\n## 注意事项\n请核对信息后提交。",
        format = "docx",
        fileName = documentFileName,
        orientation = "portrait",
    }),
    CancellationToken.None);
var realDocumentPath = documentResult.ArtifactPaths?.SingleOrDefault();
var realDocumentEvidence = new DeliverableExecutionEvidence(
    "huitian_create_document",
    documentResult.Success,
    documentResult.RequiresVerification,
    documentResult.ArtifactPaths ?? [],
    documentResult.Text,
    documentResult.ResultCode);
var realDocumentVerification = DeliverableCompletionVerifier.Verify(
    [documentRequirement],
    [realDocumentEvidence]);
if (!realDocumentVerification.Succeeded)
{
    Console.WriteLine(
        $"DOCUMENT DEBUG success={documentResult.Success}; verify={documentResult.RequiresVerification}; " +
        $"code={documentResult.ResultCode}; paths={string.Join('|', documentResult.ArtifactPaths ?? [])}; " +
        $"missing={string.Join('|', realDocumentVerification.Missing)}");
}
Assert(
    documentResult is { Success: true, RequiresVerification: false, ResultCode: "artifact_verified" } &&
    !string.IsNullOrWhiteSpace(realDocumentPath) &&
    realDocumentVerification.Succeeded,
    "the real document adapter must create, reopen, and satisfy a DOCX deliverable");
Assert(
    !DeliverableCompletionVerifier.Verify(
        [
            documentRequirement,
            new IntentDeliverableRequirement(
                IntentDeliverableKind.Document,
                "另一份独立的可打印资料",
                "docx"),
        ],
        [realDocumentEvidence]).Succeeded,
    "one artifact path must not be reused to satisfy two independent deliverables");

var conversationalRequirement = new IntentDeliverableRequirement(
    IntentDeliverableKind.ConversationalAnswer,
    "当前问题的文字答复");
var researchRequirement = new IntentDeliverableRequirement(
    IntentDeliverableKind.ResearchResult,
    "基于资料提炼出的结论");
Assert(
    DeliverableCompletionVerifier.Verify(
        [conversationalRequirement],
        [conversationEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify(
        [researchRequirement],
        [searchEvidence]).Succeeded &&
    DeliverableCompletionVerifier.Verify(
        [researchRequirement],
        [searchEvidence, conversationEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify(
        [researchRequirement],
        [searchEvidence with { ResultCode = "no_results" }, conversationEvidence]).Succeeded,
    "research must include both real source material and a synthesized answer; no-results searches must not pass");

var sendToOne = new IntentDeliverableRequirement(
    IntentDeliverableKind.ExternalDelivery,
    "正确内容已送达工作联系人",
    Destination: "微信/阿辰");
var sendEvidence = new DeliverableExecutionEvidence(
    "huitian_send_chat_message",
    Success: true,
    RequiresVerification: false,
    ArtifactPaths: [],
    RuntimeText: "消息已出现在会话记录中",
    ResultCode: "verified_sent",
    ArgumentsJson: JsonSerializer.Serialize(new { platform = "微信", contact = "阿辰" }));
Assert(
    DeliverableCompletionVerifier.Verify([sendToOne], [sendEvidence]).Succeeded &&
    !DeliverableCompletionVerifier.Verify(
        [
            sendToOne,
            new IntentDeliverableRequirement(
                IntentDeliverableKind.ExternalDelivery,
                "同一内容另送达另一名联系人",
                Destination: "小林"),
        ],
        [sendEvidence]).Succeeded,
    "external delivery evidence must match its recipient and cannot be consumed twice");

Assert(!DeliverableCompletionVerifier.Verify(
        [sendToOne with { Destination = "工作群里的阿辰" }], [sendEvidence]).Succeeded,
    "a free-form group/person description must not be satisfied by substring matching a direct-message contact");

File.Delete(fakeDocxPath);
if (!string.IsNullOrWhiteSpace(realDocumentPath) && File.Exists(realDocumentPath))
{
    File.Delete(realDocumentPath);
}

Console.WriteLine("PASS: structured intent, app/execution memory, performance profiling, routing, and summaries");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidDataException(message);
}
