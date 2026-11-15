using System.Text.RegularExpressions;

namespace HuiTian.Infrastructure;

/// <summary>
/// Aggregate capability signals only. This result never contains page text,
/// profile names, message bodies, exact addresses or automation IDs.
/// </summary>
internal sealed record SoulPageCapability(
    bool AuthenticHost,
    bool LoginWallDetected,
    bool ChatPageVisible,
    bool StableConversationSemantics,
    bool StableMessageSemantics)
{
    public bool CanEstablishReadOnlyReplyBaseline =>
        AuthenticHost && !LoginWallDetected && ChatPageVisible &&
        StableConversationSemantics && StableMessageSemantics;
}

internal static partial class SoulPageCapabilityAnalyzer
{
    internal static bool IsExplicitSoulWindow(string? windowTitle) =>
        !string.IsNullOrWhiteSpace(windowTitle) &&
        SoulWindowNameRegex().IsMatch(windowTitle);

    internal static SoulPageCapability Analyze(SoulAccessibilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var address = snapshot.PageAddress;
        var candidate = address?.Contains("://", StringComparison.Ordinal) == true
            ? address : "https://" + address;
        var parsed = Uri.TryCreate(candidate, UriKind.Absolute, out var uri);
        var authenticHost = parsed && uri is not null &&
            uri.Scheme is "http" or "https" &&
            (uri.Host.Equals("soulapp.cn", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".soulapp.cn", StringComparison.OrdinalIgnoreCase));
        var path = uri?.AbsolutePath ?? string.Empty;
        var chatPage = authenticHost &&
            (path.Contains("message", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/im", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/im/", StringComparison.OrdinalIgnoreCase));
        var nodes = snapshot.Nodes;
        var loginWall = nodes.Any(node =>
            node.Role is "Dialog" or "Window" &&
            ContainsLoginWallPrompt(node.Name)) ||
            nodes.Any(node =>
                node.Role is "Group" or "Pane" &&
                node.AutomationId is "login-modal" or "login-dialog" &&
                ContainsLoginWallPrompt(node.Name));

        var roots = Enumerable.Range(0, nodes.Count)
            .Where(index => nodes[index].Role is "Group" or "Pane" or "Document" &&
                            nodes[index].AutomationId is "conversation-view" or
                                "conversation-panel" or "chat-view" or "chat-panel")
            .ToArray();
        var conversationSemantics = false;
        var messageSemantics = false;
        if (roots.Length == 1)
        {
            var root = roots[0];
            var headers = Enumerable.Range(0, nodes.Count)
                .Where(index => IsDescendant(nodes, index, root) &&
                    nodes[index].Role is "Text" or "Header" or "Heading" &&
                    nodes[index].AutomationId is "conversation-title" or
                        "chat-title" or "conversation-header" or "chat-header" &&
                    !string.IsNullOrWhiteSpace(nodes[index].Name))
                .ToArray();
            var lists = Enumerable.Range(0, nodes.Count)
                .Where(index => IsDescendant(nodes, index, root) &&
                    nodes[index].Role is "List" or "Group" &&
                    nodes[index].AutomationId is "message-list" or
                        "chat-history" or "conversation-messages")
                .ToArray();
            conversationSemantics = headers.Length == 1 && lists.Length == 1;
            if (conversationSemantics)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var messageContainers = Enumerable.Range(0, nodes.Count)
                    .Where(index => IsDescendant(nodes, index, lists[0]) &&
                                    nodes[index].Role is "ListItem" or "Group")
                    .ToArray();
                messageSemantics = messageContainers.Length > 0 &&
                    messageContainers.All(index =>
                        IsDirectedEventId(nodes[index].AutomationId) &&
                        ids.Add(nodes[index].AutomationId));
            }
        }
        return new SoulPageCapability(authenticHost, loginWall, chatPage,
            conversationSemantics && !snapshot.Truncated,
            messageSemantics && !snapshot.Truncated);
    }

    internal static string Format(SoulPageCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var host = capability.AuthenticHost ? "已核验" : "未核验";
        var login = capability.LoginWallDetected ? "检测到" : "未检测到（不代表已登录）";
        var page = capability.ChatPageVisible ? "消息页" : "未确认消息页";
        var conversation = capability.StableConversationSemantics ? "可核验" : "未暴露";
        var messages = capability.StableMessageSemantics ? "可核验" : "未暴露";
        var conclusion = capability.CanEstablishReadOnlyReplyBaseline
            ? "可建立只读新消息基线；仍不证明可发送或支持多人聊天"
            : "无法安全确认新回复；不能据此继续多人聊天";
        return $"[Soul只读能力] 站点：{host}；登录墙：{login}；页面：{page}；会话语义：{conversation}；消息方向与事件ID：{messages}；{conclusion}。";
    }

    internal static async Task<DynamicToolResult> AppendIfNamedAsync(
        string? windowTitle,
        DynamicToolResult original,
        ISoulAccessibilitySnapshotSource source,
        CancellationToken cancellationToken)
    {
        if (!IsExplicitSoulWindow(windowTitle)) return original;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1200));
        string diagnostic;
        try
        {
            var snapshot = await source.CaptureAsync(budget.Token).ConfigureAwait(false);
            diagnostic = snapshot is null
                ? "[Soul只读能力] 页面辅助功能结构暂时不可读取；不能确认登录、会话或新回复，也不能据此继续多人聊天。"
                : Format(Analyze(snapshot));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostic = "[Soul只读能力] 只读检查超过短时限；不能确认会话或新回复，也不能据此继续多人聊天。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostic = "[Soul只读能力] 只读检查未能完成；不能确认会话或新回复，也不能据此继续多人聊天。";
        }
        return original with { Text = original.Text.TrimEnd() + "\n" + diagnostic };
    }

    private static bool ContainsLoginWallPrompt(string value) =>
        value.Contains("扫码登录", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("请先登录", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("验证码登录", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("scan to log in", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectedEventId(string id)
    {
        const string incoming = "message-incoming-";
        const string outgoing = "message-outgoing-";
        var prefix = id.StartsWith(incoming, StringComparison.OrdinalIgnoreCase)
            ? incoming : id.StartsWith(outgoing, StringComparison.OrdinalIgnoreCase)
                ? outgoing : null;
        if (prefix is null) return false;
        var suffix = id[prefix.Length..];
        return suffix.Length is >= 4 and <= 100 && suffix.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsDescendant(
        IReadOnlyList<SoulAccessibilityNode> nodes,
        int index,
        int ancestor)
    {
        var parent = nodes[index].ParentIndex;
        for (var depth = 0; parent >= 0 && depth < 32; depth++)
        {
            if (parent == ancestor) return true;
            if (parent >= nodes.Count || parent >= index) return false;
            parent = nodes[parent].ParentIndex;
        }
        return false;
    }

    [GeneratedRegex(@"(?i)(?<![a-z])soul(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SoulWindowNameRegex();
}
