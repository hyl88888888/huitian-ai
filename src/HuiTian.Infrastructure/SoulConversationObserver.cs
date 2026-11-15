using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace HuiTian.Infrastructure;

public enum SoulObservationStatus
{
    BaselineCaptured,
    NoNewReply,
    NewReply,
    WindowUnavailable,
    PlatformUnverified,
    ConversationUnverified,
    ConversationChanged,
    MessageSemanticsUnavailable,
}

public sealed record SoulAccessibilityNode(
    int ParentIndex,
    string Role,
    string Name,
    string AutomationId);

public sealed record SoulAccessibilitySnapshot(
    string WindowTitle,
    string? PageAddress,
    IReadOnlyList<SoulAccessibilityNode> Nodes,
    bool Truncated = false,
    int WindowHandle = 0);

public sealed record SoulObservedMessage(
    string EventId,
    bool IsIncoming,
    string Text);

public sealed record SoulConversationObservation(
    SoulObservationStatus Status,
    string Detail,
    string? ConversationTitle,
    IReadOnlyList<SoulObservedMessage> NewReplies);

public interface ISoulAccessibilitySnapshotSource
{
    Task<SoulAccessibilitySnapshot?> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads only the accessibility tree of an already open Soul browser window.
/// It never activates a window, clicks, types, scrolls or captures pixels.
/// A bounded pending read prevents UIA hangs from spawning repeated workers.
/// </summary>
public sealed class WindowsSoulAccessibilitySnapshotSource : ISoulAccessibilitySnapshotSource
{
    private readonly object _gate = new();
    private Task<SoulAccessibilitySnapshot?>? _pending;

    public async Task<SoulAccessibilitySnapshot?> CaptureAsync(
        CancellationToken cancellationToken)
    {
        Task<SoulAccessibilitySnapshot?> pending;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) return null;
            pending = _pending = Task.Run(ReadVisibleSoulWindow, CancellationToken.None);
        }
        try
        {
            return await pending.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
    }

    private static SoulAccessibilitySnapshot? ReadVisibleSoulWindow()
    {
        try
        {
            var window = WindowsUiAutomation.FindWindow("Soul");
            if (window is null) return null;
            var title = window.Current.Name ?? string.Empty;
            var nodes = new List<SoulAccessibilityNode>();
            var queue = new Queue<(AutomationElement Element, int ParentIndex, int Depth)>();
            queue.Enqueue((window, -1, 0));
            var walker = TreeWalker.ControlViewWalker;
            var timer = Stopwatch.StartNew();
            string? address = null;
            const int maxNodes = 1200;
            const int maxDepth = 18;
            while (queue.Count > 0 && nodes.Count < maxNodes && timer.ElapsedMilliseconds < 1400)
            {
                var (element, parent, depth) = queue.Dequeue();
                AutomationElement.AutomationElementInformation current;
                try { current = element.Current; }
                catch (ElementNotAvailableException) { continue; }
                catch (InvalidOperationException) { continue; }
                catch (COMException) { continue; }
                if (current.IsOffscreen) continue;
                var role = current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                var name = Bounded(current.Name, 1000);
                var automationId = Bounded(current.AutomationId, 180);
                var index = nodes.Count;
                nodes.Add(new SoulAccessibilityNode(parent, role, name, automationId));
                if (address is null && role == "Edit" &&
                    (name.Contains("地址", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                     automationId.Contains("address", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                            address = Bounded(((ValuePattern)pattern).Current.Value, 2048);
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }
                    catch (COMException) { }
                }
                if (depth >= maxDepth) continue;
                try
                {
                    var child = walker.GetFirstChild(element);
                    var siblings = 0;
                    while (child is not null && siblings++ < 300 &&
                           nodes.Count + queue.Count < maxNodes &&
                           timer.ElapsedMilliseconds < 1400)
                    {
                        queue.Enqueue((child, index, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
            return new SoulAccessibilitySnapshot(title, address, nodes,
                Truncated: queue.Count > 0,
                WindowHandle: window.Current.NativeWindowHandle);
        }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (COMException) { return null; }
    }

    private static string Bounded(string? value, int limit) =>
        value is null ? string.Empty : value.Length <= limit ? value : value[..limit];
}

/// <summary>
/// Requires an authentic Soul address, a semantic conversation container and
/// header, and message nodes with explicit incoming/outgoing IDs. Browser UIA
/// often exposes only visual text; in that case this returns uncertainty rather
/// than treating a sidebar preview or our own bubble as a new reply.
/// </summary>
public sealed class SoulConversationObserver(ISoulAccessibilitySnapshotSource source)
{
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private string? _boundTitle;
    private int _boundWindowHandle;
    private HashSet<string>? _seenEventIds;

    public async Task<SoulConversationObservation> ObserveAsync(
        string expectedConversationTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConversationTitle);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveLockedAsync(expectedConversationTitle, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<SoulConversationObservation> ObserveLockedAsync(
        string expectedConversationTitle,
        CancellationToken cancellationToken)
    {
        SoulAccessibilitySnapshot? snapshot;
        try { snapshot = await source.CaptureAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return Uncertain(SoulObservationStatus.WindowUnavailable,
                "Soul 辅助功能树读取失败，不能确认会话内容。");
        }
        if (snapshot is null)
            return Uncertain(SoulObservationStatus.WindowUnavailable,
                "Soul 窗口或辅助功能树暂时不可读取。");
        if (!IsAuthenticSoulAddress(snapshot.PageAddress))
            return Uncertain(SoulObservationStatus.PlatformUnverified,
                "无法从浏览器地址栏核验当前页面属于 Soul。");
        if (snapshot.WindowHandle == 0)
            return Uncertain(SoulObservationStatus.ConversationUnverified,
                "无法绑定当前浏览器窗口。");
        if (_boundWindowHandle != 0 && snapshot.WindowHandle != _boundWindowHandle)
            return Uncertain(SoulObservationStatus.ConversationChanged,
                "Soul 浏览器窗口已变化，旧会话基线不能复用。");
        if (snapshot.Truncated)
            return Uncertain(SoulObservationStatus.MessageSemanticsUnavailable,
                "辅助功能树读取不完整，不能判断消息方向或新旧。");

        var nodes = snapshot.Nodes;
        var conversationRoots = Enumerable.Range(0, nodes.Count)
            .Where(index => IsConversationRoot(nodes[index]))
            .ToArray();
        if (conversationRoots.Length != 1)
            return Uncertain(SoulObservationStatus.ConversationUnverified,
                "无法唯一定位当前会话区域。");
        var root = conversationRoots[0];
        var headers = Enumerable.Range(0, nodes.Count)
            .Where(index => IsDescendant(nodes, index, root) && IsConversationHeader(nodes[index]))
            .Select(index => Normalize(nodes[index].Name))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (headers.Length != 1)
            return Uncertain(SoulObservationStatus.ConversationUnverified,
                "当前会话标题无法唯一核验。");
        var title = headers[0];
        if (!string.Equals(title, Normalize(expectedConversationTitle), StringComparison.Ordinal) ||
            _boundTitle is not null &&
            !string.Equals(title, _boundTitle, StringComparison.Ordinal))
            return Uncertain(SoulObservationStatus.ConversationChanged,
                "可见会话标题与指定对象不同，已停止读取新回复。", title);

        var lists = Enumerable.Range(0, nodes.Count)
            .Where(index => IsDescendant(nodes, index, root) && IsMessageList(nodes[index]))
            .ToArray();
        if (lists.Length != 1)
            return Uncertain(SoulObservationStatus.MessageSemanticsUnavailable,
                "无法唯一定位当前会话消息列表。", title);
        var list = lists[0];
        var messages = new List<SoulObservedMessage>();
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        var messageCandidates = Enumerable.Range(0, nodes.Count)
            .Where(index => IsDescendant(nodes, index, list) &&
                            IsMessageCandidate(nodes[index]))
            .ToArray();
        if (messageCandidates.Length == 0 &&
            Enumerable.Range(0, nodes.Count).Any(index =>
                IsDescendant(nodes, index, list) &&
                nodes[index].Role == "Text" &&
                !string.IsNullOrWhiteSpace(nodes[index].Name)))
            return Uncertain(SoulObservationStatus.MessageSemanticsUnavailable,
                "消息列表有可见文字，但没有可核验的消息方向和事件标识。", title);
        foreach (var index in messageCandidates)
        {
            if (!TryParseMessage(nodes, index, out var message) ||
                !uniqueIds.Add(message.EventId))
                return Uncertain(SoulObservationStatus.MessageSemanticsUnavailable,
                    "消息缺少可核验的方向、内容或事件标识。", title);
            messages.Add(message);
        }

        if (_seenEventIds is null)
        {
            _boundTitle = title;
            _boundWindowHandle = snapshot.WindowHandle;
            _seenEventIds = messages.Select(message => message.EventId)
                .ToHashSet(StringComparer.Ordinal);
            return new SoulConversationObservation(SoulObservationStatus.BaselineCaptured,
                "已记录当前可读消息作为基线；旧气泡不算新回复。", title, []);
        }
        var replies = messages
            .Where(message => !_seenEventIds.Contains(message.EventId) && message.IsIncoming)
            .ToArray();
        foreach (var message in messages) _seenEventIds.Add(message.EventId);
        return new SoulConversationObservation(
            replies.Length > 0 ? SoulObservationStatus.NewReply : SoulObservationStatus.NoNewReply,
            replies.Length > 0 ? "读取到指定会话的新入站消息。" : "当前没有可核验的新入站消息。",
            title, replies);
    }

    public void Reset()
    {
        _boundTitle = null;
        _boundWindowHandle = 0;
        _seenEventIds = null;
    }

    private static bool IsAuthenticSoulAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var candidate = address.Contains("://", StringComparison.Ordinal)
            ? address : "https://" + address;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" &&
               (uri.Host.Equals("soulapp.cn", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".soulapp.cn", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConversationRoot(SoulAccessibilityNode node) =>
        node.Role is "Group" or "Pane" or "Document" &&
        node.AutomationId is "conversation-view" or "conversation-panel" or
            "chat-view" or "chat-panel";

    private static bool IsConversationHeader(SoulAccessibilityNode node) =>
        node.Role is "Text" or "Header" or "Heading" &&
        node.AutomationId is "conversation-title" or "chat-title" or
            "conversation-header" or "chat-header";

    private static bool IsMessageList(SoulAccessibilityNode node) =>
        node.Role is "List" or "Group" &&
        node.AutomationId is "message-list" or "chat-history" or
            "conversation-messages";

    private static bool IsMessageCandidate(SoulAccessibilityNode node) =>
        node.Role is "ListItem" or "Group" &&
        node.AutomationId.StartsWith("message-", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseMessage(
        IReadOnlyList<SoulAccessibilityNode> nodes,
        int index,
        out SoulObservedMessage message)
    {
        message = null!;
        var node = nodes[index];
        const string incomingPrefix = "message-incoming-";
        const string outgoingPrefix = "message-outgoing-";
        var incoming = node.AutomationId.StartsWith(incomingPrefix,
            StringComparison.OrdinalIgnoreCase);
        var outgoing = node.AutomationId.StartsWith(outgoingPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (!incoming && !outgoing) return false;
        var id = node.AutomationId[(incoming ? incomingPrefix : outgoingPrefix).Length..];
        if (id.Length is < 4 or > 100 ||
            id.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        var text = Normalize(node.Name);
        if (text.Length == 0)
        {
            var labels = Enumerable.Range(0, nodes.Count)
                .Where(child => IsDescendant(nodes, child, index) &&
                                nodes[child].Role == "Text" &&
                                nodes[child].AutomationId is "message-content" or "chat-content")
                .Select(child => Normalize(nodes[child].Name))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (labels.Length != 1) return false;
            text = labels[0];
        }
        if (text.Length > 1000) return false;
        message = new SoulObservedMessage(node.AutomationId, incoming, text);
        return true;
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

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

    private static SoulConversationObservation Uncertain(
        SoulObservationStatus status,
        string detail,
        string? title = null) => new(status, detail, title, []);
}
