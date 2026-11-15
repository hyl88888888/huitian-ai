using System.Text.Json;
using System.Text.Encodings.Web;

namespace HuiTian.Infrastructure;

internal enum DouyinDiscoveryPageKind
{
    MixedSearchResults,
    UserSearchResults,
    UserProfile,
    LoginRequired,
    NoResults,
}

/// <summary>
/// Read-only interpretation of accessible browser controls. A search card or
/// profile page is never treated as a screened person or a sent conversation.
/// </summary>
internal sealed record DouyinDiscoveryPageState(
    DouyinDiscoveryPageKind Kind,
    bool UsersTabAvailable,
    int ProfileEntryCount)
{
    private static readonly JsonSerializerOptions ObservationJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal string ToCompactObservation()
    {
        var next = Kind switch
        {
            DouyinDiscoveryPageKind.LoginRequired => "请用户本人完成登录，再重新观察页面",
            DouyinDiscoveryPageKind.NoResults => "调整查询条件后重新观察结果",
            DouyinDiscoveryPageKind.MixedSearchResults when UsersTabAvailable =>
                "可选择当前页面的“用户”标签，再观察真实用户结果",
            DouyinDiscoveryPageKind.MixedSearchResults =>
                "当前没有可核对的用户入口；检查平台实际开放的用户或同城入口",
            DouyinDiscoveryPageKind.UserSearchResults =>
                "打开一位真实用户的资料页，再核对稳定账号 ID、成年及免费聊天资格",
            _ => "核对资料页的稳定账号 ID、成年及免费聊天资格后再筛选",
        };
        return "抖音页面状态：" + JsonSerializer.Serialize(new
        {
            stage = Kind.ToString(),
            usersTabLocator = UsersTabAvailable ? "用户" : null,
            visibleProfileEntries = ProfileEntryCount,
            verifiedCandidates = 0,
            next,
        }, ObservationJsonOptions);
    }
}

internal static class DouyinDiscoveryPageAnalyzer
{
    internal static DouyinDiscoveryPageState? Analyze(
        string windowTitle,
        IReadOnlyList<WindowsUiAutomation.InspectionControl> controls)
    {
        var address = controls
            .Where(control => control.Role == "Edit" &&
                (control.Name.Contains("地址", StringComparison.OrdinalIgnoreCase) ||
                 control.Name.Contains("address", StringComparison.OrdinalIgnoreCase)))
            .Select(control => ParseDouyinUri(control.Value))
            .FirstOrDefault(uri => uri is not null);
        if (address is null &&
            !windowTitle.Contains("抖音", StringComparison.OrdinalIgnoreCase) &&
            !windowTitle.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            return null;

        var login = UiInterventionDetector.Detect(controls.Select(control =>
            new VisibleUiControl(control.Role, control.Name, control.IsPassword)).ToArray());
        if (login?.Title == "需要你完成登录")
            return new(DouyinDiscoveryPageKind.LoginRequired, false, 0);

        var isProfile = address is not null && IsUserProfile(address);
        if (isProfile)
            return new(DouyinDiscoveryPageKind.UserProfile, false, 0);

        var isSearch = address is not null &&
            address.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("抖音搜索", StringComparison.OrdinalIgnoreCase);
        if (!isSearch) return null;

        var usersTabs = controls.Where(control =>
            control.Role is "TabItem" or "Button" or "Hyperlink" &&
            control.Name.Equals("用户", StringComparison.Ordinal) && control.Enabled).ToArray();
        var usersTabAvailable = usersTabs.Length == 1;
        var selectedUsersTab = usersTabs.Any(control => control.IsSelected == true) ||
            GetQueryValue(address, "type")?.Equals("user", StringComparison.OrdinalIgnoreCase) == true;
        var noResults = controls.Any(control =>
            control.Role is "Text" or "Document" or "Group" &&
            new[] { "暂无相关结果", "暂无搜索结果", "没有找到相关", "未找到相关用户", "搜索结果为空" }
                .Any(cue => control.Name.Contains(cue, StringComparison.OrdinalIgnoreCase)));
        if (noResults)
            return new(DouyinDiscoveryPageKind.NoResults, usersTabAvailable, 0);

        var profileEntryCount = controls.Count(control =>
            control.Role is "Hyperlink" or "Button" &&
            ParseDouyinUri(control.Value) is { } uri && IsUserProfile(uri));
        return new(
            selectedUsersTab
                ? DouyinDiscoveryPageKind.UserSearchResults
                : DouyinDiscoveryPageKind.MixedSearchResults,
            usersTabAvailable,
            profileEntryCount);
    }

    private static bool IsUserProfile(Uri uri) =>
        uri.AbsolutePath.StartsWith("/user/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Length > "/user/".Length;

    private static Uri? ParseDouyinUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var candidate = raw.Trim();
        if (candidate.StartsWith("douyin.com/", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("www.douyin.com/", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.Host is not "douyin.com" &&
            !uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase))
            return null;
        return uri;
    }

    private static string? GetQueryValue(Uri? uri, string key)
    {
        if (uri is null) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
}
