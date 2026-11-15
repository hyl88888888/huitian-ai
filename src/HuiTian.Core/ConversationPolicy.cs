using System.Text;
using System.Text.RegularExpressions;

namespace HuiTian.Core;

public enum AgentTaskRoute
{
    DirectAnswer,
    Research,
    FileWork,
    Presentation,
    ComputerControl,
    Composite,
}

public sealed record ConversationMessageSnapshot(
    string Role,
    string Text,
    IReadOnlyList<string>? Attachments = null);

public sealed record CompositeTaskIntent(
    string Location,
    string Platform,
    string Recipient);

public sealed record ApplicationLaunchIntent(string Application);

/// <summary>
/// Keeps each user turn focused on the newest request.  The policy is kept in
/// Core so both the desktop UI and the local protocol bridge make the same
/// routing decision.
/// </summary>
public static partial class ConversationPolicy
{
    private const string RoutePrefix = "[回天任务路由:";

    private static readonly string[] ComputerActionWords =
    [
        "打开", "关闭", "点击", "双击", "滚动", "切换", "操作", "输入", "打字",
        "粘贴", "拖进", "拖入", "拖到", "播放", "暂停", "切歌", "安装", "下载",
        "发送", "发消息", "发给", "查找", "搜索", "选择", "填写", "编辑", "修改",
        "创建", "新建", "保存", "导出", "上传", "发布", "提交", "回复", "联系",
        "聊天", "沟通", "整理", "删除", "复制", "移动", "重命名", "转换", "截图",
        "截屏", "录屏", "剪辑", "剪掉", "裁剪", "改成", "调整", "移除", "去掉",
        "打包", "解包", "查订单", "填表",
    ];

    private static readonly string[] ComputerTargets =
    [
        "电脑", "桌面", "窗口", "程序", "软件", "浏览器", "网页", "任务栏",
        "微信", "WeChat", "QQ", "联系人", "输入框", "文件资源管理器", "歌曲", "音乐",
        "应用", "播放器", "视频", "照片", "系统设置", "剪贴板", "邮件", "邮箱",
        "Word", "Excel", "PPT", "PowerPoint", "Office",
    ];

    private static readonly string[] FileNouns =
    [
        "文件", "文件夹", "目录", "附件", "文档", "文本", "代码", "表格", "Excel",
        "Word", "CSV", "JSON", "PDF", "图片", "照片", "PPT", "演示文稿", "幻灯片",
        "桌面",
    ];

    private static readonly string[] FileActionWords =
    [
        "保存", "另存", "创建", "新建", "写入", "导出", "生成文件", "生成文档",
        "移动", "复制到", "拷贝到", "复制文件", "拷贝文件", "重命名", "改名", "压缩", "解压", "删除", "整理", "归类",
        "分类", "收拾", "搬到", "挪到", "放到", "查找文件", "读取文件", "打开文件",
        "转换", "转成", "转换成", "合并", "拆分", "打包", "解包", "裁剪",
    ];

    public static AgentTaskRoute Classify(
        string? text,
        bool hasAttachments = false,
        IReadOnlyList<ConversationMessageSnapshot>? priorMessages = null)
    {
        var value = NormalizeUserInput(text);
        if (InformationalActionQuestionRegex().IsMatch(value) &&
            !DirectExecutionCueRegex().IsMatch(value))
        {
            return AgentTaskRoute.DirectAnswer;
        }

        var feedbackOnly = IsFeedbackOnly(value);
        var presentationRequested = !feedbackOnly && ExplicitlyRequestsPresentation(value);
        var fileWorkRequested = !feedbackOnly && ExplicitlyRequestsFileWork(value);
        var computerWorkRequested = !feedbackOnly && ExplicitlyRequestsComputerWork(value);
        if (fileWorkRequested && computerWorkRequested &&
            !ChatFastPathParser.LooksLikeActionRequest(value) &&
            !SendToContactRegex().IsMatch(value))
        {
            // FileWork already exposes native file operations plus UI tools.
            // Counting ordinary "save to desktop" wording as a second
            // computer capability turns a single fast file task into a slower
            // composite workflow without adding any useful executor.
            computerWorkRequested = false;
        }
        var researchRequested = !feedbackOnly && ResearchRequestRegex().IsMatch(value);
        var requestedCapabilities =
            (presentationRequested ? 1 : 0) +
            (fileWorkRequested ? 1 : 0) +
            (computerWorkRequested ? 1 : 0) +
            (researchRequested ? 1 : 0);

        // A multi-stage request must not be squeezed into one capability and
        // lose tools needed by later stages (for example research -> document
        // -> chat delivery).  Composite is an execution route, not a billing
        // or UI mode; it simply preserves all explicitly requested abilities.
        if (requestedCapabilities >= 2)
        {
            return AgentTaskRoute.Composite;
        }

        if (presentationRequested)
        {
            return AgentTaskRoute.Presentation;
        }

        if (fileWorkRequested)
        {
            return AgentTaskRoute.FileWork;
        }

        if (computerWorkRequested)
        {
            return AgentTaskRoute.ComputerControl;
        }

        if (researchRequested)
        {
            return AgentTaskRoute.Research;
        }

        if (hasAttachments)
        {
            return AgentTaskRoute.FileWork;
        }

        if (feedbackOnly || priorMessages is not { Count: > 0 } ||
            !ContextualActionRegex().IsMatch(value))
        {
            return AgentTaskRoute.DirectAnswer;
        }

        // Natural follow-ups often omit the object and application because a
        // person can recover them from the immediately preceding exchange:
        // “按这个来”, “把它放过去”, “重新弄一下”.  Inherit only an earlier
        // non-chat route and only when this turn contains a real continuation
        // action. Pure praise, criticism and emotion never trigger tools.
        foreach (var message in priorMessages.Reverse())
        {
            if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var priorRoute = Classify(
                message.Text,
                message.Attachments is { Count: > 0 });
            if (priorRoute != AgentTaskRoute.DirectAnswer)
            {
                return priorRoute;
            }
        }

        return AgentTaskRoute.DirectAnswer;
    }

    private static bool ExplicitlyRequestsComputerWork(string value)
    {
        if (ChatFastPathParser.LooksLikeActionRequest(value) ||
            SendToContactRegex().IsMatch(value) ||
            NaturalComputerRequestRegex().IsMatch(value) ||
            GeneralApplicationOperationRegex().IsMatch(value))
        {
            return true;
        }

        var hasAction = ComputerActionWords.Any(word =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase));
        var hasTarget = ComputerTargets.Any(word =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase));
        return hasAction && hasTarget;
    }

    public static bool ExplicitlyRequestsFileWork(string? text)
    {
        // A user will often make the desired boundary explicit with wording
        // such as "只在对话里回答，不要创建文件".  Looking for the words
        // "创建" and "文件" independently turned that safety boundary into a
        // file-writing request.  Remove only negated file actions first; any
        // later positive clause (for example "不要放 C 盘，请在 D 盘创建")
        // remains available for the normal positive checks below.
        var value = NegatedFileActionRegex().Replace(text ?? string.Empty, string.Empty);
        var hasFileNoun = FileNouns.Any(word =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase));
        var hasFileAction = FileActionWords.Any(word =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase));
        var hasExplicitPath = WindowsPathRegex().IsMatch(value);
        return (hasFileNoun && hasFileAction) ||
               hasExplicitPath && hasFileAction ||
               ExplicitSaveRegex().IsMatch(value) ||
               PronounFileActionRegex().IsMatch(value);
    }

    public static bool ExplicitlyRequestsPresentation(string? text)
    {
        var value = text ?? string.Empty;
        var hasPresentationNoun = value.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("演示文稿", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("幻灯片", StringComparison.OrdinalIgnoreCase);
        return hasPresentationNoun &&
               (PresentationActionRegex().IsMatch(value) ||
                PresentationRevisionRegex().IsMatch(value));
    }

    /// <summary>
    /// Returns true only when deterministic routing cannot safely distinguish
    /// a conversational response from a real-world action.  High-confidence
    /// answers and high-confidence actions remain instant; this bounded
    /// fallback asks the already-running local brain to classify meaning.
    /// </summary>
    public static bool NeedsSemanticIntentResolution(
        string? text,
        AgentTaskRoute deterministicRoute,
        IReadOnlyList<ConversationMessageSnapshot>? priorMessages = null)
    {
        if (deterministicRoute != AgentTaskRoute.DirectAnswer)
        {
            return false;
        }

        var value = NormalizeUserInput(text);
        if (value.Length is < 2 or > 1200 ||
            InformationalActionQuestionRegex().IsMatch(value) ||
            IsFeedbackOnly(value) ||
            ClearCommentOrObservationRegex().IsMatch(value) ||
            ClearTextOnlyRequestRegex().IsMatch(value))
        {
            return false;
        }

        if (AmbiguousExecutionCueRegex().IsMatch(value) ||
            ActionTargetReferenceRegex().IsMatch(value) ||
            ExternalContinuationRegex().IsMatch(value) ||
            IntentBoundaryCueRegex().IsMatch(value))
        {
            return true;
        }

        return priorMessages is { Count: > 0 } &&
               ContextualActionRegex().IsMatch(value) &&
               priorMessages.TakeLast(4).Any(message =>
                   message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                   Classify(message.Text, message.Attachments is { Count: > 0 }) !=
                   AgentTaskRoute.DirectAnswer);
    }

    public static AgentTaskRoute ApplySemanticIntent(
        AgentTaskRoute deterministicRoute,
        SemanticIntentDecision? decision)
    {
        if (decision is null || !decision.IsConfident)
        {
            return deterministicRoute;
        }

        return decision.Mode switch
        {
            SemanticIntentMode.Answer or SemanticIntentMode.Clarify =>
                AgentTaskRoute.DirectAnswer,
            SemanticIntentMode.Mixed => AgentTaskRoute.Composite,
            SemanticIntentMode.Operate => decision.Domain switch
            {
                SemanticIntentDomain.File => AgentTaskRoute.FileWork,
                SemanticIntentDomain.Office => AgentTaskRoute.FileWork,
                SemanticIntentDomain.Research => AgentTaskRoute.Research,
                _ => AgentTaskRoute.ComputerControl,
            },
            _ => deterministicRoute,
        };
    }

    /// <summary>
    /// Recognizes the commercial workflow “research current facts ->
    /// synthesize -> create a presentation -> deliver it to a chat contact”.
    /// All stages and an explicit recipient must be present before the
    /// optimized contract is enabled.
    /// </summary>
    public static bool TryParseCompositeTask(string? text, out CompositeTaskIntent intent)
    {
        intent = new CompositeTaskIntent(string.Empty, string.Empty, string.Empty);
        var value = NormalizeUserInput(text);
        if (!ExplicitlyRequestsPresentation(value) ||
            !value.Contains("天气", StringComparison.OrdinalIgnoreCase) ||
            !(value.Contains("攻略", StringComparison.OrdinalIgnoreCase) ||
              value.Contains("旅行", StringComparison.OrdinalIgnoreCase) ||
              value.Contains("旅游", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var locationMatch = WeatherLocationRegex().Match(value);
        var deliveryMatch = PresentationDeliveryRegex().Match(value);
        if (!locationMatch.Success || !deliveryMatch.Success)
        {
            return false;
        }

        var location = locationMatch.Groups["location"].Value.Trim();
        var platform = deliveryMatch.Groups["platform"].Value.Trim();
        var recipient = deliveryMatch.Groups["recipient"].Value.Trim()
            .TrimEnd('。', '！', '!', '？', '?');
        if (location.Length is < 2 or > 18 || recipient.Length is < 1 or > 30)
        {
            return false;
        }

        intent = new CompositeTaskIntent(location, platform, recipient);
        return true;
    }

    /// <summary>
    /// Recognizes only a single, self-contained request to open an installed
    /// Windows application.  Requests that also ask to search, type, play,
    /// install, download, or message remain on the normal agent route so this
    /// shortcut can never silently discard the rest of the user's task.
    /// </summary>
    public static bool TryParseApplicationLaunch(
        string? text,
        out ApplicationLaunchIntent intent)
    {
        intent = new ApplicationLaunchIntent(string.Empty);
        var value = NormalizeUserInput(text).Trim();
        var match = ApplicationLaunchRegex().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var application = match.Groups["application"].Value.Trim();
        application = ApplicationPoliteSuffixRegex().Replace(application, string.Empty).Trim();
        application = ApplicationNounSuffixRegex().Replace(application, string.Empty).Trim();
        if (application.Length is < 1 or > 80 ||
            UnsafeApplicationTargetRegex().IsMatch(application) ||
            ApplicationContinuationRegex().IsMatch(application) ||
            application.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            application.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        intent = new ApplicationLaunchIntent(application);
        return true;
    }

    public static string Marker(AgentTaskRoute route) =>
        $"{RoutePrefix}{RouteName(route)}]";

    public static string ExtractCurrentRequest(string? text)
    {
        var value = text ?? string.Empty;
        const string requestMarker = "【当前轮次唯一要求】";
        const string boundaryMarker = "【本轮边界】";
        const string universalBoundaryMarker = "【本轮语义边界尚未预判】";
        var start = value.LastIndexOf(requestMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return value.Trim();
        }

        start += requestMarker.Length;
        var declaredBoundary = value.IndexOf(boundaryMarker, start, StringComparison.Ordinal);
        var universalBoundary = value.IndexOf(
            universalBoundaryMarker,
            start,
            StringComparison.Ordinal);
        var end = declaredBoundary < 0
            ? universalBoundary
            : universalBoundary < 0
                ? declaredBoundary
                : Math.Min(declaredBoundary, universalBoundary);
        return (end < 0 ? value[start..] : value[start..end]).Trim();
    }

    public static AgentTaskRoute? FindDeclaredRoute(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = RouteRegex().Matches(text).Cast<Match>().LastOrDefault();
        return match?.Groups[1].Value switch
        {
            "answer" => AgentTaskRoute.DirectAnswer,
            "research" => AgentTaskRoute.Research,
            "file" => AgentTaskRoute.FileWork,
            "presentation" => AgentTaskRoute.Presentation,
            "computer" => AgentTaskRoute.ComputerControl,
            "composite" => AgentTaskRoute.Composite,
            _ => null,
        };
    }

    public static bool IsToolAllowed(string toolName, AgentTaskRoute route)
    {
        // The compact executor is the only model-facing tool on the universal
        // semantic route. Specific legacy routes keep concrete tools.
        if (toolName == "huitian_execute")
        {
            return route == AgentTaskRoute.Composite;
        }

        return route switch
        {
        AgentTaskRoute.DirectAnswer => false,
        AgentTaskRoute.Research => toolName is
            "huitian_search_web" or "huitian_open_browser" or
            "huitian_screen_social_candidate" or "huitian_read_social_candidates" or
            "huitian_inspect_windows" or "huitian_activate_window" or
            "huitian_capture_screen" or "huitian_click_control" or
            "huitian_scroll_window",
        AgentTaskRoute.FileWork => toolName is not
            ("huitian_create_presentation" or "huitian_search_web" or "update_plan"),
        AgentTaskRoute.Presentation => toolName is
            "huitian_create_presentation" or "huitian_search_web" or
            "huitian_send_chat_files" or
            "huitian_search_files" or "huitian_read_text_file" or
            "huitian_read_document" or
            "huitian_open_application" or "huitian_inspect_windows" or
            "huitian_activate_window" or "huitian_capture_screen" or
            "huitian_click_control" or "huitian_type_text" or
            "huitian_click_point" or "huitian_type_active" or
            "huitian_press_keys" or "update_plan",
        AgentTaskRoute.ComputerControl => toolName is not
            ("huitian_write_text_file" or "huitian_create_presentation" or
             "huitian_move_file"),
        AgentTaskRoute.Composite => true,
        _ => false,
        };
    }

    public static string BuildTurnContract(string currentRequest, AgentTaskRoute route)
    {
        var normalizedRequest = NormalizeUserInput(currentRequest);
        if (route == AgentTaskRoute.DirectAnswer)
        {
            // Tool-free turns need neither computer recovery instructions nor
            // a legacy composite-task parser. Keep only answer constraints;
            // identity and general conversational context live in the base
            // instructions, not duplicated on each request.
            return $$"""
                {{Marker(route)}}
                【当前轮次唯一要求】
                {{normalizedRequest}}
                【本轮边界】
                只在对话中回答，不操作电脑、不创建文件、不声称已执行。结合上下文理解对上一结果不足的反馈、情绪与省略，不是要求解释这些词的字面含义；方向明确时直接修正。长文用不同观点、事实和例证充实，禁止同义反复或凑字数。保留指定原文，只回答本轮，不输出思考或重复标签。
                """;
        }
        var boundary = route switch
        {
            AgentTaskRoute.DirectAnswer =>
                "直接在对话中回答。不得调用电脑或文件工具；除非用户明确说保存、创建、导出或给出目标文件路径，否则不得创建文件。写文案、标题、方案、解释和问答默认都只返回可复制文字。若要求长文，先按主题、受众和结构覆盖不同观点与例证；达到字数要靠新增有效信息，禁止同义反复、循环段落或为凑字数重复。输出前检查并删除重复内容。",
            AgentTaskRoute.Research =>
                "只为当前问题查询必要的最新公开信息，核对后直接给出结论；不得创建文件。",
            AgentTaskRoute.FileWork =>
                "只处理用户明确指定的文件；需要产出文件时保存到用户指定位置并报告完整路径，不得顺带创建无关文件。",
            AgentTaskRoute.Presentation =>
                "制作用户明确要求的演示文稿，完成后核验文件存在并报告完整路径。",
            AgentTaskRoute.ComputerControl =>
                "第一步立即调用能直接推进结果的能力，不先写计划或复述要求。按最短可靠路径操作当前目标程序；已在运行就直接激活，没有运行才启动。每次界面改变后重新观察；只有看到预期结果才可报告成功。搜索项或联系人找不到时，先检查高置信的同音字、近音字和识别错误；若只有一个合理候选就继续，仍有多个候选且选错会影响结果时再简短询问用户。",
            AgentTaskRoute.Composite =>
                "这是包含多个目标的完整任务。先建立有依赖关系的短计划：互不依赖的联网查询和只读文件读取可以并行；内容生成必须等待所需资料；文件发送和界面操作必须等待产物核验并按顺序执行。不得完成第一步后停下，也不得因一个步骤失败丢掉其余仍可完成的步骤。",
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

        var pragmaticGuidance = FeedbackOrEmotionRegex().IsMatch(normalizedRequest)
            ? "这句话是自然对话中的评价、纠正或情绪表达，不是要求解释这些词的字面含义。先结合紧邻上下文判断：若用户在批评上一结果，能确定改进方向时直接给出修正后的结果，不要只道歉、辩解或复述批评；若用户在表达个人感受、赞许或感谢，则像人一样简短自然回应。"
            : CorrectionIntentRegex().IsMatch(normalizedRequest)
                ? "用户正在纠正此前的理解。优先采用“不是……而是……”“我说的是……”之后的真实目标，不得继续沿用被否定的字面解释。"
                : ContextualActionRegex().IsMatch(normalizedRequest)
                    ? "这是承接前文的省略表达。结合最近对话还原“这个、那个、它、这么做、发过去”等对象；对象和动作能唯一确定时直接完成，不要反问已经说过的信息。"
                    : string.Empty;

        if (route != AgentTaskRoute.DirectAnswer)
        {
            pragmaticGuidance += " 当前要求需要在电脑、应用或文件中产生真实结果；必须调用相应能力并核验结果，不能只回答操作方法、口头承诺或把任务改写成说明文字。";
        }

        var composite = TryParseCompositeTask(normalizedRequest, out var compositeIntent)
            ? $$"""

                 【复合任务快速流水线】
                 这是一个完整任务，不要停在第一步，也不要用多轮桌面截图读取网页：
                 1. 立即并行调用联网搜索，分别查询“{{compositeIntent.Location}}未来7天天气”和“{{compositeIntent.Location}}旅行攻略 交通 景点 住宿 建议”；直接使用工具返回的可读结果。
                 2. 一次性提炼天气趋势、行程建议、风险提示和实用清单，不反复搜索相同内容。
                 3. 立即调用演示文稿能力生成真实 PPT，并记录工具返回的完整文件路径。
                 4. 立即使用聊天文件发送专用能力，把该路径发送给{{compositeIntent.Platform}}联系人“{{compositeIntent.Recipient}}”；不得用多轮坐标点击。
                 5. 任一步失败只换一种合理方式重试一次；最终明确报告已完成步骤、未完成步骤和文件位置，绝不能无进展等待。
                 """
            : string.Empty;

        return $$"""
                 {{Marker(route)}}
                 【当前轮次唯一要求】
                 {{normalizedRequest}}
                 【本轮边界】
                 {{boundary}}
                 {{composite}}
                 {{pragmaticGuidance}}
                 只处理“当前轮次唯一要求”。历史内容仅用于理解指代，不得继续回答已结束的旧问题，不得复述或输出内部思考过程。用户输入可能来自语音或输入法：要结合整句、上下文和语气理解明显错别字、同音字、口语省略及“不够……”这类对上一结果不足的反馈；高置信时直接按真实意图回答，不要机械照抄错误字面，也不要擅自改动用户明确要求逐字保留的原文。联系人或搜索目标找不到时，先尝试唯一合理的同音字、近音字或识别纠正；只有仍有多个合理候选且选错会实质改变结果时才询问。最终只给用户当前问题的结果；不要加“最终回答”“回天”等重复标签。
                 """;
    }

    /// <summary>
    /// The initial contract for the universal agent path.  It deliberately
    /// does not classify the user's words in product code: the same model turn
    /// that answers or executes must first decide the semantic success state.
    /// </summary>
    public static string BuildUniversalTurnContract(string currentRequest)
    {
        var normalizedRequest = NormalizeUserInput(currentRequest);
        return $$"""
                 {{Marker(AgentTaskRoute.Composite)}}
                 【当前轮次唯一要求】
                 {{normalizedRequest}}
                 【本轮语义边界尚未预判】
                 先理解用户最终要得到的结果，而不是寻找关键词或套固定句式。系统会从本轮要求建立 goal；你必须通过通用语义出口声明全部 deliverables 并选择当前能力：若最终结果本来就是回答、解释、闲聊、评价或可复制文字，选择 respond；若最终必须让电脑、应用、文件、网页、影音或外部通信出现真实变化，选择最能推进最终交付物的真实能力。搜索、阅读、计划和草稿只是中间步骤，不能冒充用户要求的正式文件、修改结果或外部送达。调度层只会在全部交付物取得真实证据后登记完成。能从最近上下文可靠解析的省略和指代直接解析；只有关键对象确实存在多个合理选择且选错会产生实质影响时，才询问一个最小问题。
                 只处理当前轮次唯一要求；最近内容只用于理解指代、语气和修正后的目标。最终不输出内部思考、“最终回答”或重复身份标签。
                 """;
    }

    public static bool IsFeedbackOrEmotion(string? text) =>
        FeedbackOrEmotionRegex().IsMatch(NormalizeUserInput(text));

    private static bool IsFeedbackOnly(string value) =>
        FeedbackOrEmotionRegex().IsMatch(value) &&
        !ActionOrRepairCueRegex().IsMatch(value) &&
        !ResearchRequestRegex().IsMatch(value);

    public static string NormalizeUserInput(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (LiteralTextRequestRegex().IsMatch(value))
        {
            return value;
        }

        value = value.Replace("回填", "回天", StringComparison.OrdinalIgnoreCase);

        // These are high-confidence input-method/ASR slips only. Contextual or
        // domain-specific words are deliberately left to the model so quoted
        // text and names are never silently rewritten.
        if ((value.Contains("鼠标", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("位置", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("不精准", StringComparison.OrdinalIgnoreCase)) &&
            value.Contains("电机", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Replace("电机", "点击", StringComparison.OrdinalIgnoreCase);
        }

        value = EllipsisRegex().Replace(value, "……");
        return value;
    }

    public static string SanitizeAssistantText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Replace("\r\n", "\n").Replace('\r', '\n');
        value = ClosedThinkingBlockRegex().Replace(value, string.Empty);
        value = ClosedAnalysisBlockRegex().Replace(value, string.Empty);

        var finalMarkers = FinalAnswerLabelRegex().Matches(value);
        if (finalMarkers.Count > 0)
        {
            var finalMarker = finalMarkers[^1];
            value = value[(finalMarker.Index + finalMarker.Length)..];
        }
        else if (UnwrappedReasoningHeaderRegex().IsMatch(value))
        {
            // Some local chat templates expose reasoning without <think>
            // tags. Keep it hidden until a real final-answer boundary arrives.
            return string.Empty;
        }

        var openThink = value.LastIndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var closeThink = value.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (openThink > closeThink)
        {
            value = value[..openThink];
        }

        var openAnalysis = value.LastIndexOf("<analysis>", StringComparison.OrdinalIgnoreCase);
        var closeAnalysis = value.LastIndexOf("</analysis>", StringComparison.OrdinalIgnoreCase);
        if (openAnalysis > closeAnalysis)
        {
            value = value[..openAnalysis];
        }

        value = ChatTemplateTokenRegex().Replace(value, string.Empty);
        value = FinalAnswerLabelRegex().Replace(value, string.Empty);
        value = RepeatedBlankLinesRegex().Replace(value, "\n\n");
        value = RemoveRepeatedLongParagraphs(value);
        return value.Trim();
    }

    private static string RemoveRepeatedLongParagraphs(string value)
    {
        // Long local generations occasionally repeat a full paragraph to reach
        // a requested word count. Remove only exact, substantive repeats so
        // headings, short list labels and intentional refrains remain intact.
        if (value.Length < 800)
        {
            return value;
        }

        var paragraphs = value.Split("\n\n", StringSplitOptions.None);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(paragraphs.Length);
        foreach (var paragraph in paragraphs)
        {
            var key = Regex.Replace(paragraph, @"\s+", " ").Trim();
            if (key.Length >= 40 && !seen.Add(key))
            {
                continue;
            }

            kept.Add(paragraph);
        }

        return string.Join("\n\n", kept);
    }

    public static bool TryExtractExplicitPreference(string? message, out string preference)
    {
        preference = string.Empty;
        var value = Regex.Replace((message ?? string.Empty).Trim(), @"\s+", " ");
        if (value.Length is < 3 or > 240 || !ExplicitPreferenceRegex().IsMatch(value))
        {
            return false;
        }

        // A long passage offered for rewriting can contain words such as
        // “以后” inside the passage.  It is content, not a product preference.
        if (value.Length > 140 && value.Contains('：') &&
            !value.StartsWith("以后", StringComparison.Ordinal) &&
            !value.StartsWith("今后", StringComparison.Ordinal) &&
            !value.StartsWith("请记住", StringComparison.Ordinal))
        {
            return false;
        }

        preference = value;
        return true;
    }

    public static string BuildConversationSummary(
        IEnumerable<ConversationMessageSnapshot> messages,
        int maximumLength = 320)
    {
        var materialized = messages
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();
        var allRequests = materialized
            .Where(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            .Select(item => Compact(item.Text, 56))
            .Where(IsSubstantiveRequest)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requests = allRequests.TakeLast(5).ToArray();
        var latestResult = materialized
            .Where(item => item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .Select(item => Compact(SanitizeAssistantText(item.Text), 90))
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (requests.Length == 0)
        {
            return Compact(latestResult ?? string.Empty, maximumLength);
        }

        string summary;
        if (allRequests.Length == 1)
        {
            summary = $"围绕“{requests[0]}”进行处理";
        }
        else if (allRequests.Length == 2)
        {
            summary = $"先处理“{requests[0]}”，随后处理“{requests[1]}”";
        }
        else
        {
            var recentDescription = string.Join("、", requests.Select(request => $"“{request}”"));
            summary = allRequests.Length > requests.Length
                ? $"持续推进相关任务，最近处理了{recentDescription}等内容"
                : $"围绕{recentDescription}依次推进";
        }

        if (!string.IsNullOrWhiteSpace(latestResult))
        {
            summary += $"。最新进展：{latestResult}";
        }

        return Compact(summary, maximumLength);
    }

    public static string BuildNaturalTaskTitle(
        IEnumerable<string> requests,
        int maximumLength = 34)
    {
        var firstRequest = requests
            .Select(NormalizeUserInput)
            .Select(value => CourtesyPrefixRegex().Replace(value, string.Empty).Trim())
            .FirstOrDefault(IsSubstantiveRequest);
        if (string.IsNullOrWhiteSpace(firstRequest))
        {
            return "新的本机任务";
        }

        // Like a human-written sidebar title, describe the subject once. Do
        // not append mechanical counters such as “等 3 项”.
        return Compact(firstRequest, maximumLength);
    }

    public static string BuildResumeContext(
        IEnumerable<ConversationMessageSnapshot> messages,
        int maximumCharacters = 1400)
    {
        var selected = messages
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .TakeLast(8)
            .Select(item => new ConversationMessageSnapshot(
                item.Role,
                item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? SanitizeAssistantText(item.Text)
                    : item.Text.Trim(),
                item.Attachments))
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();
        if (selected.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in selected.Reverse())
        {
            var label = item.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "用户"
                : "回天";
            var attachmentText = item.Attachments is { Count: > 0 }
                ? "（当时附加：" + string.Join("；", item.Attachments.TakeLast(4)) + "）"
                : string.Empty;
            var line = $"{label}：{Compact(item.Text, 260)}{attachmentText}";
            if (builder.Length + line.Length + 1 > maximumCharacters)
            {
                break;
            }

            builder.Insert(0, line + "\n");
        }

        return builder.ToString().TrimEnd();
    }

    public static bool ReferencesPriorAttachments(string? text)
    {
        var value = text ?? string.Empty;
        string[] references =
        [
            "刚才那张图", "刚才这张图", "刚才的图", "上一张图", "上张图", "之前的图",
            "刚才那个文件", "刚才的文件", "上个文件", "之前的文件", "刚才的附件",
            "之前的附件", "继续处理附件", "继续处理刚才", "把附件发", "将附件发",
        ];
        return references.Any(reference =>
            value.Contains(reference, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ReferencesPriorConversation(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        string[] references =
        [
            "继续", "接着", "刚才", "上面", "前面", "上一", "之前", "这段", "这句",
            "这个", "那个", "它", "其内容", "再给", "再写", "再来", "换一个", "改成",
            "改短", "改长", "重写", "润色", "优化一下", "按这个", "基于此",
        ];
        return references.Any(reference =>
            value.Contains(reference, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSubstantiveRequest(string value)
    {
        var normalized = Regex.Replace(value, @"[\s，。！？,.!?~嗯啊哦好的收到]+", string.Empty);
        return normalized.Length >= 3;
    }

    private static string Compact(string value, int maximumLength)
    {
        var compact = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return compact.Length <= maximumLength
            ? compact
            : compact[..Math.Max(1, maximumLength - 1)] + "…";
    }

    private static string RouteName(AgentTaskRoute route) => route switch
    {
        AgentTaskRoute.DirectAnswer => "answer",
        AgentTaskRoute.Research => "research",
        AgentTaskRoute.FileWork => "file",
        AgentTaskRoute.Presentation => "presentation",
        AgentTaskRoute.ComputerControl => "computer",
        AgentTaskRoute.Composite => "composite",
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    [GeneratedRegex(@"\[回天任务路由:(answer|research|file|presentation|computer|composite)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RouteRegex();

    [GeneratedRegex(@"(?is)<think>.*?</think>")]
    private static partial Regex ClosedThinkingBlockRegex();

    [GeneratedRegex(@"(?is)<analysis>.*?</analysis>")]
    private static partial Regex ClosedAnalysisBlockRegex();

    [GeneratedRegex(@"(?im)^\s*(?:#{1,4}\s*)?(?:最终回答|final answer)\s*[:：]?\s*")]
    private static partial Regex FinalAnswerLabelRegex();

    [GeneratedRegex(@"(?im)^\s*(?:#{1,4}\s*)?(?:思考过程|分析过程|推理过程|reasoning|analysis)\s*[:：]?\s*")]
    private static partial Regex UnwrappedReasoningHeaderRegex();

    [GeneratedRegex(@"(?i)<\|(?:im_start|im_end|assistant|user|endoftext)\|>")]
    private static partial Regex ChatTemplateTokenRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RepeatedBlankLinesRegex();

    [GeneratedRegex(@"(?:\.{2,}|。{2,}|．{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisRegex();

    [GeneratedRegex(@"^(?:(?:请|麻烦|劳驾)(?:你)?(?:帮我|给我)?|(?:先)?帮我|给我)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CourtesyPrefixRegex();

    [GeneratedRegex(@"(?:逐字|原文|原样|不要修改|不要纠正|别改|就是我打的字|按字面意思)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiteralTextRequestRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?:保存|另存|创建|新建|写入|导出|生成).{0,10}(?:文件|文档|文本|代码|表格)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitSaveRegex();

    [GeneratedRegex(@"(?:不要|不用|无需|不需要|别|请勿)[^，。；;！？!?\r\n]{0,18}?(?:保存|另存|创建|新建|写入|导出|生成(?:文件|文档)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedFileActionRegex();

    [GeneratedRegex(@"(?:做|制作|创建|生成|新建|设计|帮我做|给我做)(?!的|过).{0,8}(?:PPT|PowerPoint|演示文稿|幻灯片)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresentationActionRegex();

    [GeneratedRegex(@"(?:(?:PPT|PowerPoint|演示文稿|幻灯片).{0,18}(?:重做|重新做|修改|改一下|调整|优化|美化|完善)|(?:重做|重新做|修改|调整|优化|美化|完善).{0,18}(?:PPT|PowerPoint|演示文稿|幻灯片))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresentationRevisionRegex();

    [GeneratedRegex(@"(?:(?:把|将)?(?:它|这个|那个|这些|那些|上面的内容|刚才(?:那个|的内容|的结果|做好的))(?:给我)?\s*(?:保存|另存|放到|移到|移动到|挪到|搬到|复制到|整理|归类|重命名|改名|导出|压缩|解压)|(?:桌面|文件夹|目录).{0,18}(?:太乱|很乱|乱了|乱死).{0,18}(?:收拾|整理|归类|分类))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PronounFileActionRegex();

    [GeneratedRegex(@"(?:(?:请|麻烦|劳驾|帮我|替我|给我|我要你|你去|直接|马上|现在).{0,24}(?:打开|关闭|点击|双击|滚动|切换|操作|输入|打字|粘贴|拖入|拖到|播放|暂停|切歌|安装|下载|发送|发消息)|(?:在|去)(?:电脑|桌面|窗口|程序|软件|浏览器|网页|微信|QQ|任务栏).{0,24}(?:打开|关闭|找|搜索|点击|输入|发送|播放|操作)|(?:放|播放)(?:一首|首|点)?[^，。！？!?]{0,20}(?:歌|音乐))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalComputerRequestRegex();

    // General application grammar for software that HuiTian has never seen
    // before.  It deliberately recognizes the relationship between an
    // imperative, an application/location phrase and an observable action;
    // it does not maintain a brittle list of product names or buttons.
    [GeneratedRegex(
        @"(?:(?:请|麻烦|劳驾|帮我|替我|给我|你来|直接|马上|现在)?\s*(?:在|用|打开|切到|进入)(?!(?:\s*(?:[A-Za-z]:\\|[A-Za-z]\s*盘|文件(?:夹)?|目录|桌面|下载文件夹)))\s*[^，。！？!?]{1,36}?(?:里|中|上|软件|应用|程序|客户端|页面|窗口)?[^，。！？!?]{0,48}?(?:打开|关闭|查找|搜索|查询|点击|选择|输入|填写|填表|编辑|修改|改成|调整|创建|新建|删除|移除|去掉|上传|下载|保存|导出|播放|暂停|发送|回复|发布|提交|设置|整理|转换|压缩|解压|打包|解包|剪辑|剪掉|裁剪|截图|截屏|录屏))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex GeneralApplicationOperationRegex();

    [GeneratedRegex(@"(?:(?:联网|上网|网上)?(?:查|查询|搜|搜索|找|了解|核实|看看).{0,40}(?:最新|今天|现在|实时|新闻|天气|价格|汇率|政策|官网|资料|信息|攻略)|(?:最新|今天|现在|实时|未来[^，。！？!?]{0,16}天气|[^，。！？!?]{0,16}天气|价格|汇率|政策|新闻).{0,28}(?:怎么样|多少|是什么|如何|有没有|有吗|吗|呢|[？?])|^未来[^，。！？!?]{0,20}天气[。！!]?$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchRequestRegex();

    [GeneratedRegex(@"(?:继续|接着|然后|下一步|照这个|按这个|就按|这么做|这样做|照办|执行|去做|直接做|重新|重做|重来|再弄|再试|改一下|调整一下|优化一下|完善一下|把(?:它|这个|那个|这些|那些)|将(?:它|这个|那个)|发过去|发给|发送给|放过去|移过去|存下来|弄好|处理掉)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualActionRegex();

    [GeneratedRegex(@"(?:不是.{0,40}(?:而是|是要|是让|我要)|我(?:说|指|要)的是|我是(?:要|让)你|我让你|我的意思是|你(?:理解|听|弄)错|没理解我的意思|答非所问)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionIntentRegex();

    [GeneratedRegex(@"(?:我(?:觉得|感觉|认为|有点|很|太)|我(?:心情|现在|今天).{0,10}(?:难受|难过|开心|烦|累|生气|失望)|你(?:这(?:个|次)?|刚才|之前)?(?:回答|答案|结果|做得|说得)|这个(?:回答|答案|结果|效果)|不够|不太对|不对|错了|错误|答非所问|没听懂|没理解|理解错|敷衍|生硬|机械|不自然|没人味|不人性化|失望|生气|难过|满意|不满意|喜欢|讨厌|不错|很好|真棒|谢谢|辛苦|无语|离谱|太差|太慢|太笨|太好了)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedbackOrEmotionRegex();

    [GeneratedRegex(@"(?:(?:请|帮我|替我|给我|麻烦|劳驾|我要你|需要你|你得|你要|应该|我是(?:要|让)你|我让你)|(?:重新|重做|重写|修改|改一下|修复|优化|调整|完善|继续|接着|再来|再试|照办|执行|去做|别只|不要只|而是)|(?:^|[，,。；;])\s*(?:(?:把|将).{0,18})?(?:打开|关闭|点击|发送|保存|创建|整理|收拾|写|回答|解释|介绍|总结|起草|生成))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionOrRepairCueRegex();

    [GeneratedRegex(@"(?:(?:给|向).{1,30}(?:发|发送)(?:消息|一句|一条|文件)?|(?:发给|发送给).{1,30})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SendToContactRegex();

    [GeneratedRegex(@"(?:^|[，,。；;])\s*(?:如何|怎么|怎样|用什么办法|有什么办法|告诉我怎么)[^，。；;！？!?]{0,48}(?:打开|关闭|点击|发送|发给|保存|创建|制作|修改|整理|安装|下载|操作|使用)|(?:打开|关闭|点击|发送|保存|创建|制作|修改|整理|安装|下载|操作).{0,20}(?:的方法|的教程|怎么做|如何做)[？?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InformationalActionQuestionRegex();

    [GeneratedRegex(@"(?:帮我|替我|你来|请你直接|直接(?:帮我|替我|操作|执行|打开|关闭|点击|发送|保存|创建|制作|修改|整理)|现在就|马上(?:帮我|替我|操作|执行))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectExecutionCueRegex();

    [GeneratedRegex(@"(?:只(?:在对话|回答|回复)|不要(?:操作|打开|发送|保存|创建)|不用(?:操作|打开|发送|保存|创建)|帮我(?:写|改写|润色|翻译|解释)|给我(?:写|想|起)|(?:写|改写|润色|翻译|解释|介绍|评价|分析|起草).{0,18}(?:文案|文字|标题|句子|答案|方案|内容))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClearTextOnlyRequestRegex();

    [GeneratedRegex(@"(?:我觉得|我感觉|感觉|我发现|看起来|听起来|说起来|吐槽|评价|真|太|很|有点|越来越).{0,28}(?:难用|麻烦|好用|不好用|卡|慢|烦|奇怪|不自然|不准确|不方便|不错|挺好)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClearCommentOrObservationRegex();

    [GeneratedRegex(@"(?:你(?:去|来|直接)|替我|帮我处理|帮我弄|帮我搞|给我处理|给我弄|直接处理|直接弄|实际处理|执行一下|安排一下|办一下|解决一下|想办法完成|不要只回答|别只回答|不是让你回答)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmbiguousExecutionCueRegex();

    [GeneratedRegex(@"(?:电脑|桌面|任务栏|窗口|程序|软件|应用|浏览器|网页|网站|微信|WeChat|QQ|联系人|聊天框|输入框|屏幕|文件|文件夹|目录|附件|文档|Word|Excel|PPT|演示文稿|图片|照片|音乐|歌曲|播放器|系统设置|剪贴板)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionTargetReferenceRegex();

    [GeneratedRegex(@"(?:发过去|发给他|发给她|发送过去|放过去|移过去|存下来|直接执行|替我办|帮我办|把它弄好|把这个弄好|处理掉)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalContinuationRegex();

    [GeneratedRegex(@"(?:^|[，,。；;！？!?])\s*(?:我想|我要|我希望|我准备|我打算|我需要|能不能帮我|可不可以帮我|替我|帮我|给我)\s*[^，。；;！？!?]{1,120}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntentBoundaryCueRegex();

    [GeneratedRegex(@"(?:(?:查(?:下|一下)?|查询|看看)\s*(?:未来)?|未来)(?<location>[\p{L}\p{N}·]{2,18}?)(?:的)?天气",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherLocationRegex();

    [GeneratedRegex(@"(?:发给|发送给|给|向)\s*(?<platform>微信|QQ)(?:里的|中的|联系人|里)?\s*(?<recipient>[^，。；,;]{1,30})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresentationDeliveryRegex();

    [GeneratedRegex(@"^(?:(?:请|麻烦|劳驾)(?:你)?(?:帮我|给我)?|(?:先)?帮我|给我)?\s*(?:打开|启动|运行)\s*(?:一下|下)?\s*(?<application>[^\r\n，。！？!?；;]{1,96})\s*[。！!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationLaunchRegex();

    [GeneratedRegex(@"\s*(?:好吗|可以吗|行吗|吧)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationPoliteSuffixRegex();

    [GeneratedRegex(@"\s*(?:应用程序|程序|软件|应用)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNounSuffixRegex();

    [GeneratedRegex(@"(?:然后|并且|同时|随后|接着|搜索|查询|查找|播放|输入|打字|点击|发送|发消息|下载|安装|登录|登陆|注册|付款|支付|转账|创建|新建|编辑|写入|关闭|切换)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationContinuationRegex();

    [GeneratedRegex(@"(?:给|向).{0,30}(?:发|发送)|[|&<>`""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeApplicationTargetRegex();

    [GeneratedRegex(@"^(?:以后|今后|请记住|记住|我喜欢|我习惯|我希望你|默认|每次(?:都)?|所有任务|不要|请不要|回答时|操作时|回天.{0,12}(?:要|不要|优先)|你.{0,12}(?:要|不要|优先))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPreferenceRegex();
}
