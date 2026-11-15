using HuiTian.Core;

Assert(
    ConversationPolicy.Classify("帮我写一段产品文案") == AgentTaskRoute.DirectAnswer,
    "copywriting without a save request must stay in chat");
foreach (var request in new[]
         {
             "请只在对话里写一句山西文旅宣传语，不要创建文件",
             "写一段可以复制的文案，不用保存文件",
             "给我三个标题，无需导出文档",
             "直接回答，不需要新建文本",
             "帮我写方案，别生成文件",
         })
{
    Assert(
        ConversationPolicy.Classify(request) == AgentTaskRoute.DirectAnswer,
        $"a negated file action must stay in chat: {request}");
}
Assert(
    ConversationPolicy.Classify("不要在 C 盘创建文件，请在 D:\\HuiTian\\UserContent 创建文件") ==
    AgentTaskRoute.FileWork,
    "a later positive file request must remain detectable");
Assert(
    !ConversationPolicy.ReferencesPriorConversation("请写一句山西文旅宣传语") &&
    ConversationPolicy.ReferencesPriorConversation("再给这句宣传语起一个标题"),
    "independent answers should omit old context while explicit follow-ups retain it");
Assert(
    ConversationPolicy.Classify("把这段文案保存成 D:\\HuiTian\\UserContent\\文案.txt") ==
    AgentTaskRoute.FileWork,
    "an explicit save path must route to file work");
Assert(
    ConversationPolicy.Classify("给微信联系人凉雨发你好") == AgentTaskRoute.ComputerControl,
    "a messaging request must route to computer control");
Assert(
    ConversationPolicy.Classify(
        "和微信里的凉雨工作号聊天，用老板的身份，目的是督促让他完成今天的工作") ==
    AgentTaskRoute.ComputerControl,
    "a goal-driven chat request must never be downgraded to a text-only answer");
var mixedRequest =
    "查一下最新的人工智能政策，整理成文档保存到 D:\\HuiTian\\UserContent\\政策.txt，再发给微信联系人凉雨";
Assert(
    ConversationPolicy.Classify(mixedRequest) == AgentTaskRoute.Composite &&
    ConversationPolicy.IsToolAllowed("huitian_search_web", AgentTaskRoute.Composite) &&
    ConversationPolicy.IsToolAllowed("huitian_write_text_file", AgentTaskRoute.Composite) &&
    ConversationPolicy.IsToolAllowed("huitian_send_chat_files", AgentTaskRoute.Composite),
    "a mixed request must preserve research, file, and delivery capabilities instead of losing later stages");

var semanticCandidate = "让微信里的小王知道明天改到九点开会";
var semanticFallbackRoute = ConversationPolicy.Classify(semanticCandidate);
Assert(
    semanticFallbackRoute == AgentTaskRoute.ComputerControl &&
    ChatFastPathParser.TryParse(semanticCandidate, hasAttachedFiles: false, out _),
    "a high-confidence chat paraphrase should route directly to execution without waiting for semantic classification");
Assert(
    ConversationPolicy.NeedsSemanticIntentResolution(
        "把它发过去",
        AgentTaskRoute.DirectAnswer),
    "an omitted-object external continuation should receive semantic routing even when prior text was conversational");
var semanticOperation = new SemanticIntentDecision(
    SemanticIntentMode.Operate,
    SemanticIntentDomain.Chat,
    0.94,
    120,
    "test");
Assert(
    ConversationPolicy.ApplySemanticIntent(semanticFallbackRoute, semanticOperation) ==
    AgentTaskRoute.ComputerControl,
    "a confident semantic action decision must expose the real computer route");
Assert(
    !ConversationPolicy.NeedsSemanticIntentResolution(
        "帮我写一段产品文案",
        AgentTaskRoute.DirectAnswer) &&
    !ConversationPolicy.NeedsSemanticIntentResolution(
        "微信聊天现在真麻烦",
        AgentTaskRoute.DirectAnswer),
    "clear writing and emotional comments must remain instant text answers");

var presentationHistory = new ConversationMessageSnapshot[]
{
    new("user", "帮我做一份山西文旅PPT"),
    new("assistant", "演示文稿已经生成。"),
};
Assert(
    ConversationPolicy.Classify(
        "这个太生硬了，重新弄一下",
        priorMessages: presentationHistory) == AgentTaskRoute.Presentation,
    "an omitted-object revision must inherit the preceding presentation route");
Assert(
    ConversationPolicy.Classify(
        "就按这个来",
        priorMessages: presentationHistory) == AgentTaskRoute.Presentation,
    "a natural action continuation must retain the preceding tool route");
Assert(
    ConversationPolicy.Classify(
        "就这样吧",
        priorMessages: presentationHistory) == AgentTaskRoute.DirectAnswer,
    "a conversational acknowledgement must not inherit tools merely because prior work used them");
Assert(
    ConversationPolicy.Classify("你做的PPT太差了") == AgentTaskRoute.DirectAnswer &&
    ConversationPolicy.Classify("这个PPT太差了，重新做一下") == AgentTaskRoute.Presentation,
    "pure criticism must stay conversational while an explicit revision must perform real work");
Assert(
    ConversationPolicy.Classify("桌面文件太乱了，帮我收拾一下") == AgentTaskRoute.FileWork &&
    ConversationPolicy.Classify("把它放到桌面上") == AgentTaskRoute.FileWork,
    "colloquial file organization and pronoun-based placement must expose file tools");
Assert(
    ConversationPolicy.Classify("微信这个软件最近真难用") == AgentTaskRoute.DirectAnswer &&
    ConversationPolicy.Classify("你刚才打开微信的方式太慢了") == AgentTaskRoute.DirectAnswer &&
    ConversationPolicy.Classify("打开微信") == AgentTaskRoute.ComputerControl,
    "mentioning an application in feedback must not operate it without an action request");
Assert(
    ConversationPolicy.Classify("怎么给微信联系人发送文件？") == AgentTaskRoute.DirectAnswer &&
    ConversationPolicy.Classify("不知道怎么弄，你直接帮我发送给微信联系人") ==
    AgentTaskRoute.ComputerControl,
    "an informational how-to question must not operate the computer unless direct execution is requested");
Assert(
    ConversationPolicy.Classify("你这回答的不是我要的，我是让你把文件保存到桌面") ==
    AgentTaskRoute.FileWork,
    "a natural correction must follow the replacement intent instead of the criticized wording");
Assert(
    ConversationPolicy.Classify("未来大理天气怎么样？") == AgentTaskRoute.Research &&
    ConversationPolicy.Classify("我讨厌这样的天气") == AgentTaskRoute.DirectAnswer,
    "a real current-information question must research while an emotional remark must remain conversation");
Assert(
    ConversationPolicy.IsFeedbackOrEmotion("不够自然，听着很机械") &&
    ConversationPolicy.Classify("不够自然，听着很机械") == AgentTaskRoute.DirectAnswer,
    "dissatisfaction must be treated as pragmatic feedback rather than a literal tool request");

foreach (var (request, expectedApplication) in new[]
         {
             ("打开微信", "微信"),
             ("请帮我启动一下计算器。", "计算器"),
             ("运行 Microsoft Edge", "Microsoft Edge"),
             ("麻烦你打开截图工具吧", "截图工具"),
             (@"运行 D:\Apps\HuiTianTool.exe", @"D:\Apps\HuiTianTool.exe"),
         })
{
    Assert(
        ConversationPolicy.TryParseApplicationLaunch(request, out var launch) &&
        launch.Application == expectedApplication,
        $"a single explicit launch request should use the deterministic fast path: {request}");
}

foreach (var request in new[]
         {
             "如何打开微信？",
             "打开微信给老板发消息",
             "打开浏览器搜索大理天气",
             "启动QQ并输入你好",
             "打开音乐软件播放周杰伦",
             "运行 https://example.com",
             "打开",
         })
{
    Assert(
        !ConversationPolicy.TryParseApplicationLaunch(request, out _),
        $"a question or multi-step request must not be truncated to an application launch: {request}");
}

foreach (var (request, expectedKind, expectedArgument) in new[]
         {
             ("打开浏览器搜索大理未来天气", EverydayActionKind.BrowserSearch, "大理未来天气"),
             ("打开下载文件夹", EverydayActionKind.OpenKnownFolder, "下载"),
             ("放一首歌", EverydayActionKind.MediaPlayPause, (string?)null),
             ("下一首", EverydayActionKind.MediaNext, (string?)null),
             ("音量调大", EverydayActionKind.VolumeUp, (string?)null),
         })
{
    Assert(
        EverydayTaskParser.TryParse(request, out var everyday) &&
        everyday.Kind == expectedKind &&
        everyday.Argument == expectedArgument,
        $"an unambiguous everyday task should bypass model planning: {request}");
}
Assert(
    !EverydayTaskParser.TryParse("怎么在浏览器搜索大理天气？", out _) &&
    !EverydayTaskParser.TryParse("我觉得这首歌很好听", out _),
    "questions and observations must never trigger an everyday action");

Assert(
    ChatFastPathParser.TryParse(
        "给微信里的爱加班拖欠工资的黑心老板发送消息你好",
        hasAttachedFiles: false,
        out var directWeChat) &&
    directWeChat.Platform == ChatFastPathPlatform.WeChat &&
    directWeChat.Recipient == "爱加班拖欠工资的黑心老板" &&
    directWeChat.Message == "你好" &&
    !directWeChat.UseCurrentConversation,
    "an explicit WeChat recipient and literal message should use the deterministic fast path");
Assert(
    ChatFastPathParser.TryParse(
        "给目前已打开的微信联系人的对话框里发送你好",
        hasAttachedFiles: false,
        out var currentWeChat) &&
    currentWeChat.UseCurrentConversation &&
    currentWeChat.Recipient is null &&
    currentWeChat.Message == "你好",
    "an explicitly current WeChat conversation must not invent a recipient");
Assert(
    ChatFastPathParser.TryParse(
        "把刚才做好的PPT发给微信联系人凉雨",
        hasAttachedFiles: false,
        out var artifactSend) &&
    artifactSend.Recipient == "凉雨" &&
    artifactSend.ReferencesFiles &&
    artifactSend.Message is null,
    "an existing artifact reference should resolve to files instead of literal chat text");
Assert(
    ChatFastPathParser.TryParse(
        "在QQ里给小王发消息明天九点开会",
        hasAttachedFiles: false,
        out var qqSend) &&
    qqSend.Platform == ChatFastPathPlatform.QQ &&
    qqSend.Recipient == "小王" &&
    qqSend.Message == "明天九点开会",
    "QQ should share the same high-confidence product fast path");
Assert(
    ChatFastPathParser.TryParse(
        "和微信里的凉雨工作号聊天，用老板的身份，目的是督促让他完成今天的工作",
        hasAttachedFiles: false,
        out var goalDrivenChat) &&
    goalDrivenChat.Platform == ChatFastPathPlatform.WeChat &&
    goalDrivenChat.Recipient == "凉雨工作号" &&
    goalDrivenChat.MessageComposedFromGoal &&
    goalDrivenChat.Persona == "老板" &&
    goalDrivenChat.CommunicationGoal == "督促让他完成今天的工作" &&
    goalDrivenChat.Message is not null &&
    goalDrivenChat.Message.Contains("今天的工作进度", StringComparison.Ordinal) &&
    goalDrivenChat.Message.Contains("完成后", StringComparison.Ordinal),
    "an explicit recipient, persona and goal should compile to one verified first-message send");
Assert(
    ChatFastPathParser.TryParse(
        "用QQ联系小王，提醒他明天九点参加会议",
        hasAttachedFiles: false,
        out var reminderChat) &&
    reminderChat.Recipient == "小王" &&
    reminderChat.MessageComposedFromGoal &&
    reminderChat.Message == "提醒一下，请记得明天九点参加会议。",
    "goal-driven delivery should be semantic and not tied to one contact or one platform");
Assert(
    !ChatFastPathParser.TryParse(
        "我刚才和微信里的小王聊天，觉得他说得不对",
        hasAttachedFiles: false,
        out _),
    "a statement about a past chat must not trigger an external send");
Assert(
    !ChatFastPathParser.TryParse(
        "如何给微信联系人发送消息？",
        hasAttachedFiles: false,
        out _),
    "an informational question must never perform an external send");
Assert(
    !ChatFastPathParser.TryParse(
        "总结这个附件并发给微信联系人凉雨",
        hasAttachedFiles: true,
        out _),
    "content that still needs to be generated must remain on the agent route");

Assert(
    ChatFastPathParser.TryParse(
        "替我和微信里的女朋友聊天十分钟目的是根据她实际说的内容安慰她让她开心",
        hasAttachedFiles: false,
        out var punctuationFreeChat) &&
    punctuationFreeChat.Recipient == "女朋友" &&
    punctuationFreeChat.ContinuousConversationRequested &&
    punctuationFreeChat.CommunicationPlan?.MaximumDuration == TimeSpan.FromMinutes(10),
    "punctuation-free voice input must compile into a bounded continuous chat");
var flexibleParsed = ChatFastPathParser.TryParse(
        "去微信找小雅，帮我接着聊十分钟，看她说什么再安慰她",
        hasAttachedFiles: false,
        out var flexibleChat);
Assert(
    flexibleParsed && flexibleChat is not null &&
    flexibleChat.Recipient == "小雅" &&
    flexibleChat.ContinuousConversationRequested &&
    flexibleChat.CommunicationGoal?.Contains("安慰", StringComparison.Ordinal) == true,
    "natural word order must identify the real contact and continuing purpose");
Assert(
    ChatFastPathParser.TryParse(
        "打开微信跟当前这个人继续聊五分钟，目的是确认明天几点见",
        hasAttachedFiles: false,
        out var currentContinuousChat) &&
    currentContinuousChat.UseCurrentConversation &&
    currentContinuousChat.ContinuousConversationRequested &&
    currentContinuousChat.CommunicationPlan?.UseCurrentConversation == true,
    "an explicitly open current conversation should continue without inventing a contact name");
var qqNaturalParsed = ChatFastPathParser.TryParse(
        "用QQ替我跟小王聊十分钟，把交付日期确认下来",
        hasAttachedFiles: false,
        out var qqNaturalChat);
Assert(
    qqNaturalParsed && qqNaturalChat is not null &&
    qqNaturalChat.Recipient == "小王" &&
    qqNaturalChat.ContinuousConversationRequested,
    "QQ should accept flexible persona-free continuous-chat wording");

var semanticChatDecision = new SemanticIntentDecision(
    SemanticIntentMode.Operate,
    SemanticIntentDomain.Chat,
    0.93,
    100,
    "test");
Assert(
    ChatFastPathParser.TryParseWithSemanticFallback(
        "让微信里的小王知道会议改到九点",
        hasAttachedFiles: false,
        semanticChatDecision,
        out var semanticChat) &&
    semanticChat.Recipient == "小王" &&
    semanticChat.MessageComposedFromGoal,
    "a confident semantic chat decision must be converted into an executable communication plan");
foreach (var blockedRequest in new[]
         {
             "怎么给微信里的女朋友发消息？",
             "帮我写一句安慰她的话，但先不要发送到微信",
             "微信聊天最近真麻烦",
         })
{
    Assert(
        !ChatFastPathParser.TryParseWithSemanticFallback(
            blockedRequest,
            hasAttachedFiles: false,
            semanticChatDecision,
            out _),
        $"questions, drafts and observations must never produce an external action: {blockedRequest}");
}

foreach (var computerRequest in new[]
         {
             "帮我在剪映里把这段视频前五秒剪掉",
             "在钉钉里创建明早九点的会议",
             "用Photoshop把这张照片背景改成白色",
             "在浏览器里把这个表单填好并提交",
         })
{
    Assert(
        ConversationPolicy.Classify(computerRequest) != AgentTaskRoute.DirectAnswer,
        $"a real operation in an arbitrary application must expose computer tools: {computerRequest}");
}
Assert(
    ConversationPolicy.Classify("帮我做一份产品介绍 PPT") == AgentTaskRoute.Presentation,
    "an explicit presentation request must route to the presentation tool");
Assert(
    ConversationPolicy.TryParseCompositeTask(
        "给我查下未来大理的天气，出个旅行攻略，做成ppt，发给微信里的凉雨工作号",
        out var composite) &&
    composite.Location == "大理" &&
    composite.Platform == "微信" &&
    composite.Recipient == "凉雨工作号",
    "the travel-to-PPT-to-chat workflow must preserve the exact city and recipient");
var compositeContract = ConversationPolicy.BuildTurnContract(
    "给我查下未来大理的天气，出个旅行攻略，做成ppt，发给微信里的凉雨工作号",
    AgentTaskRoute.Presentation);
Assert(
    compositeContract.Contains("复合任务快速流水线", StringComparison.Ordinal) &&
    compositeContract.Contains("不得用多轮坐标点击", StringComparison.Ordinal) &&
    ConversationPolicy.IsToolAllowed("huitian_send_chat_files", AgentTaskRoute.Presentation),
    "the composite contract must use the dedicated generated-file delivery tool");

var quotedCopy =
    "帮我给这段文案起标题，要求反认知：你换过几份工作的话，可能发现真正能带走的东西不在工牌上。公司名留在原地，做过的案例、认识的人和解决问题的办法还能跟着你。以后选工作，要关注能带走什么。";
Assert(
    !ConversationPolicy.TryExtractExplicitPreference(quotedCopy, out _),
    "quoted copy containing the word '以后' must not become a global preference");
Assert(
    ConversationPolicy.TryExtractExplicitPreference("以后写文案默认先给三个可复制标题", out var preference) &&
    preference.Contains("三个可复制标题", StringComparison.Ordinal),
    "an explicit preference should be retained");

var cleaned = ConversationPolicy.SanitizeAssistantText(
    "<think>旧问题的分析过程</think>\n最终回答：当前问题的答案");
Assert(cleaned == "当前问题的答案", "reasoning and redundant final-answer labels must be removed");

var normalizedInput = ConversationPolicy.NormalizeUserInput("回填的鼠标电机位置不精准，不够。。。");
Assert(normalizedInput == "回天的鼠标点击位置不精准，不够……",
    "high-confidence ASR/input-method mistakes should be corrected without losing tone");
Assert(ConversationPolicy.NormalizeUserInput("请逐字保留：回填。。。") == "请逐字保留：回填。。。",
    "explicitly literal text must never be silently corrected");
var semanticContract = ConversationPolicy.BuildTurnContract(
    "这段文案不够。。。写到两三千字", AgentTaskRoute.DirectAnswer);
Assert(semanticContract.Contains("对上一结果不足的反馈", StringComparison.Ordinal) &&
       semanticContract.Contains("禁止同义反复", StringComparison.Ordinal) &&
       semanticContract.Contains("不是要求解释这些词的字面含义", StringComparison.Ordinal),
    "the turn contract must explain colloquial feedback and prohibit long-form padding");
Assert(semanticContract.Length < 330 && !semanticContract.Contains("复合任务快速流水线", StringComparison.Ordinal),
    "ordinary answers must not pay for duplicate computer workflow and recovery instructions");
var actionContract = ConversationPolicy.BuildTurnContract(
    "把它放到桌面上", AgentTaskRoute.FileWork);
Assert(actionContract.Contains("产生真实结果", StringComparison.Ordinal) &&
       actionContract.Contains("不能只回答操作方法", StringComparison.Ordinal),
    "tool routes must require verified action instead of a text-only promise");
var contactContract = ConversationPolicy.BuildTurnContract(
    "给微信联系人凉雨发消息", AgentTaskRoute.ComputerControl);
Assert(contactContract.Contains("同音字", StringComparison.Ordinal) &&
       contactContract.Contains("多个候选", StringComparison.Ordinal),
    "contact/search misses should try phonetic candidates before asking the user");

var summary = ConversationPolicy.BuildConversationSummary(
[
    new("user", "先写一个标题"),
    new("assistant", "标题一"),
    new("user", "再写一段正文"),
    new("assistant", "正文已经写好"),
]);
Assert(summary.Contains("先写一个标题", StringComparison.Ordinal) &&
       summary.Contains("再写一段正文", StringComparison.Ordinal),
    "the combined summary must retain both requests");
Assert(summary.Contains("先处理", StringComparison.Ordinal) &&
       summary.Contains("随后处理", StringComparison.Ordinal) &&
       !summary.Contains("本对话包含", StringComparison.Ordinal),
    "the task summary should read naturally instead of exposing a mechanical item count");
var naturalTitle = ConversationPolicy.BuildNaturalTaskTitle(
    ["请帮我制作山西文旅PPT", "再把它发送给微信联系人"]);
Assert(naturalTitle == "制作山西文旅PPT" && !naturalTitle.Contains("等 2 项", StringComparison.Ordinal),
    "recent-task titles should describe the subject without a mechanical request count");

var repeatedParagraph = "这一段用于验证长文不会为了凑字数而整段重复，它包含足够多的有效字符，并且应该只在最终结果中保留一次。";
var longAnswer = string.Join("\n\n", Enumerable.Repeat(repeatedParagraph, 18)) +
                 "\n\n最后补充一段不同的有效内容，用来确认去重后仍然保留后续的新信息。";
var deduplicated = ConversationPolicy.SanitizeAssistantText(longAnswer);
Assert(deduplicated.IndexOf(repeatedParagraph, StringComparison.Ordinal) ==
       deduplicated.LastIndexOf(repeatedParagraph, StringComparison.Ordinal) &&
       deduplicated.Contains("最后补充", StringComparison.Ordinal),
    "exact long-paragraph loops should be removed without dropping later unique content");

Console.WriteLine("PASS: current-turn routing, clean replies, preferences, and multi-task summary");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}
