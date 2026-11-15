using System.Text.RegularExpressions;

namespace HuiTian.App;

internal sealed record SelfEvolutionResult(
    int SuccessfulRoutes,
    int FailedRoutes,
    int MergedRoutes,
    string Summary);

/// <summary>
/// Reuses only workflows whose result was observed and verified. It evolves
/// local route memory, never application source code or executable files.
/// </summary>
internal static class SelfEvolutionEngine
{
    private static readonly (string Category, string[] Anchors)[] Categories =
    [
        ("QQ", ["QQ", "腾讯QQ"]),
        ("微信", ["微信", "WeChat", "Weixin"]),
        ("即时通讯", ["联系人", "发消息", "发送消息"]),
        ("浏览器", ["浏览器", "网页", "网站", "Edge", "Chrome"]),
        ("演示文稿", ["PPT", "PowerPoint", "演示文稿", "幻灯片"]),
        ("文档", ["Word", "文档", "合同", "报告"]),
        ("表格", ["Excel", "表格", "工作簿"]),
        ("文件", ["文件", "文件夹", "目录", "整理", "查找"]),
        ("图片", ["图片", "图像", "照片", "海报"]),
        ("文案", ["文案", "标题", "脚本", "改写", "润色"]),
        ("程序", ["程序", "软件", "应用", "打开"]),
    ];

    public static string Classify(string? text)
    {
        var value = text ?? string.Empty;
        foreach (var (category, anchors) in Categories)
        {
            if (anchors.Any(anchor => value.Contains(anchor, StringComparison.OrdinalIgnoreCase)))
            {
                return category;
            }
        }

        return "通用";
    }

    public static SelfEvolutionResult Run(ProductState state)
    {
        var merged = 0;
        var normalized = new List<TaskLearning>();
        foreach (var learning in state.TaskLearnings
                     .Where(item => item.VerifiedSuccess && item.SuccessCount > 0 &&
                                    !string.IsNullOrWhiteSpace(item.Strategy))
                     .OrderByDescending(item => item.UpdatedAt))
        {
            learning.Category = ResolveCategory(learning.Trigger, learning.Category);
            learning.FailureCount = 0;
            learning.RefreshScore();
            var strategyKey = Normalize(learning.Strategy);
            var existing = normalized.FirstOrDefault(item =>
                item.Category.Equals(learning.Category, StringComparison.OrdinalIgnoreCase) &&
                Normalize(item.Strategy).Equals(strategyKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                normalized.Add(learning);
                continue;
            }

            merged++;
            var oldRuns = existing.SuccessCount;
            var incomingRuns = learning.SuccessCount;
            var totalRuns = oldRuns + incomingRuns;
            if (totalRuns > 0)
            {
                existing.AverageDurationSeconds =
                    (existing.AverageDurationSeconds * oldRuns +
                     learning.AverageDurationSeconds * incomingRuns) / totalRuns;
            }

            existing.SuccessCount += learning.SuccessCount;
            existing.FailureCount = 0;
            existing.UpdatedAt = existing.UpdatedAt >= learning.UpdatedAt
                ? existing.UpdatedAt
                : learning.UpdatedAt;
            if (learning.Trigger.Length > existing.Trigger.Length)
            {
                existing.Trigger = learning.Trigger;
            }

            if (learning.VerificationEvidence.Length > existing.VerificationEvidence.Length)
            {
                existing.VerificationEvidence = learning.VerificationEvidence;
            }

            existing.VerifiedSuccess = true;
            existing.RefreshScore();
        }

        state.TaskLearnings = normalized
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(100)
            .ToList();
        state.UserPreferences = state.UserPreferences
            .Select(value => Regex.Replace(value.Trim(), @"\s+", " "))
            .Where(value => HuiTian.Core.ConversationPolicy.TryExtractExplicitPreference(
                value,
                out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(80)
            .ToList();

        var successful = state.TaskLearnings.Count;
        state.SelfEvolutionRunCount++;
        state.LastSelfEvolutionAt = DateTimeOffset.Now;
        state.SelfEvolutionSummary = successful == 0
            ? "已完成本机自检；完成并核验真实任务后会开始积累成功路径。"
            : $"已复盘并保留 {successful} 条经过结果核验的成功路径" +
              (merged > 0 ? $"，合并 {merged} 条重复经验。" : "。");
        return new SelfEvolutionResult(successful, 0, merged, state.SelfEvolutionSummary);
    }

    public static IEnumerable<TaskLearning> FindRelevant(
        IEnumerable<TaskLearning> learnings,
        string message,
        int maximum)
    {
        var category = Classify(message);
        return learnings
            .Where(item => item.VerifiedSuccess && item.SuccessCount > 0 && item.Succeeded)
            .Select(item => BuildLearningMatch(item, message, category))
            .Where(match => match.Score >= 0.42)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Item.Confidence)
            .ThenByDescending(match => match.Item.UpdatedAt)
            .Take(maximum)
            .Select(match => match.Item);
    }

    public static bool IsPreferenceRelevant(string message, string preference)
    {
        string[] globalMarkers =
        [
            "所有任务", "每次", "回答时", "语言", "语气", "称呼", "格式", "不要确认",
        ];
        if (globalMarkers.Any(marker =>
                preference.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var messageCategory = Classify(message);
        return messageCategory != "通用" && messageCategory.Equals(
            Classify(preference),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVerifiedSuccessfulStrategy(
        string trigger,
        IReadOnlyList<string> completedActions,
        out string evidence)
    {
        evidence = string.Empty;
        var actions = completedActions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (actions.Length == 0)
        {
            return false;
        }

        var directVerified = actions.LastOrDefault(action => ContainsAny(
            action,
            "创建文件", "制作演示文稿", "整理文件", "读取文件", "读取文档",
            "搜索文件", "打开程序", "打开网页", "联网搜索", "发送消息已完成"));
        var lastMutation = Array.FindLastIndex(actions, action => ContainsAny(
            action,
            "点击", "输入文字", "使用快捷键", "切换窗口", "滚动窗口"));
        var verificationIndex = Array.FindLastIndex(actions, action =>
            ContainsAny(action, "观察屏幕", "观察窗口"));

        var isChatSend = IsChatCategory(Classify(trigger)) &&
                         ContainsAny(trigger, "发送", "发消息", "发给");
        if (isChatSend)
        {
            var dedicatedSend = actions.LastOrDefault(action => action.Contains(
                "发送消息已完成",
                StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(dedicatedSend))
            {
                evidence = dedicatedSend;
                return true;
            }

            var typed = actions.Any(action => action.Contains(
                "输入文字",
                StringComparison.OrdinalIgnoreCase));
            var sendIndex = Array.FindLastIndex(actions, action =>
                Regex.IsMatch(
                    action,
                    @"按下\s+(?:CTRL\+)?ENTER\b|已点击[:：].{0,20}(?:发送|Send)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (!typed || sendIndex < 0 || verificationIndex <= sendIndex)
            {
                return false;
            }

            evidence = "已核验联系人、输入内容和发送后的聊天画面";
            return true;
        }

        if (lastMutation >= 0)
        {
            if (verificationIndex <= lastMutation)
            {
                return false;
            }

            evidence = "界面变化后已重新观察结果";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(directVerified))
        {
            evidence = directVerified;
            return true;
        }

        return false;
    }

    private static double Similarity(
        string message,
        string trigger,
        string messageCategory,
        string triggerCategory)
    {
        if (IsPlatformChatCategory(messageCategory) &&
            IsPlatformChatCategory(triggerCategory) &&
            !messageCategory.Equals(triggerCategory, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var left = CharacterBigrams(message);
        var right = CharacterBigrams(trigger);
        var overlap = left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count();
        var dice = left.Count + right.Count == 0
            ? 0
            : 2d * overlap / (left.Count + right.Count);
        var categoryBonus = messageCategory.Equals(
            triggerCategory,
            StringComparison.OrdinalIgnoreCase) && messageCategory != "通用"
            ? 0.28
            : 0;
        return Math.Min(1, dice * 0.72 + categoryBonus);
    }

    private static LearningMatch BuildLearningMatch(
        TaskLearning item,
        string message,
        string messageCategory)
    {
        // Repair platform labels written by older versions before exposing the
        // route to the prompt, so a QQ path is never presented as WeChat memory.
        item.Category = ResolveCategory(item.Trigger, item.Category);
        return new LearningMatch(
            item,
            Similarity(message, item.Trigger, messageCategory, item.Category));
    }

    private static HashSet<string> CharacterBigrams(string value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]", string.Empty)
            .ToLowerInvariant();
        var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (normalized.Length == 1)
        {
            grams.Add(normalized);
            return grams;
        }

        for (var index = 0; index + 1 < normalized.Length; index++)
        {
            grams.Add(normalized.Substring(index, 2));
        }

        return grams;
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string ResolveCategory(string trigger, string? storedCategory)
    {
        var classified = Classify(trigger);
        return classified != "通用" || string.IsNullOrWhiteSpace(storedCategory)
            ? classified
            : storedCategory;
    }

    private static bool IsPlatformChatCategory(string category) =>
        category.Equals("微信", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("QQ", StringComparison.OrdinalIgnoreCase);

    private static bool IsChatCategory(string category) =>
        IsPlatformChatCategory(category) ||
        category.Equals("即时通讯", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => Regex.Replace(
        value ?? string.Empty,
        @"[\s→、，,。；;：:]+",
        " ").Trim();

    private sealed record LearningMatch(TaskLearning Item, double Score);
}
