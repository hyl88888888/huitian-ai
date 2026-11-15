using HuiTian.Infrastructure;

internal static class DouyinDiscoveryPageBoundary
{
    internal static void Run()
    {
        const string title = "发现更多精彩视频 - 抖音搜索 - Google Chrome";
        static WindowsUiAutomation.InspectionControl Control(
            string role, string name, string? value = null, bool? selected = null) =>
            new(role, name, "", true, IsSelected: selected, Value: value);

        var mixed = new[]
        {
            Control("Edit", "地址和搜索栏",
                "https://www.douyin.com/jingxuan/search/local?type=general"),
            Control("Button", "登录"),
            Control("TabItem", "综合", selected: true),
            Control("TabItem", "视频"),
            Control("TabItem", "用户"),
            Control("Text", "同城生活视频"),
            Control("Hyperlink", "视频作者", "https://www.douyin.com/video/123"),
        };
        var mixedState = DouyinDiscoveryPageAnalyzer.Analyze(title, mixed);
        Check(mixedState is
            { Kind: DouyinDiscoveryPageKind.MixedSearchResults,
              UsersTabAvailable: true, ProfileEntryCount: 0 },
            "mixed video results must point to the user tab without inventing a candidate");
        Check(mixedState!.ToCompactObservation().Contains("\"verifiedCandidates\":0"),
            "navigation state must not claim a screened candidate");
        var structuredObservation = WindowsUiAutomation.FormatInspectionSummary(
            title, mixed, 80, mixedState.ToCompactObservation());
        Check(structuredObservation.Length <= 2_600 &&
              structuredObservation.Contains("抖音页面状态", StringComparison.Ordinal),
            "structured page state must remain visible inside the observation budget");

        var users = new[]
        {
            Control("Edit", "地址和搜索栏",
                "https://www.douyin.com/jingxuan/search/local?type=user"),
            Control("TabItem", "用户", selected: true),
            Control("Hyperlink", "某用户", "https://www.douyin.com/user/stable123"),
            Control("Hyperlink", "某视频", "https://www.douyin.com/video/456"),
        };
        Check(DouyinDiscoveryPageAnalyzer.Analyze(title, users) is
            { Kind: DouyinDiscoveryPageKind.UserSearchResults, ProfileEntryCount: 1 },
            "only actual user-profile links may count as profile entries");

        var profile = new[]
        {
            Control("Edit", "地址和搜索栏", "https://www.douyin.com/user/stable123"),
            Control("Text", "用户资料"),
        };
        Check(DouyinDiscoveryPageAnalyzer.Analyze("个人主页 - 抖音", profile) is
            { Kind: DouyinDiscoveryPageKind.UserProfile, ProfileEntryCount: 0 },
            "a profile URL is a page stage, not an eligible candidate");

        var noResults = mixed.Append(Control("Text", "暂无相关结果")).ToArray();
        Check(DouyinDiscoveryPageAnalyzer.Analyze(title, noResults)?.Kind ==
              DouyinDiscoveryPageKind.NoResults,
            "a visible empty-results message must stop candidate inference");

        var loginWall = mixed.Concat(new[]
        {
            Control("Text", "扫码登录"),
            Control("Image", "二维码"),
        }).ToArray();
        Check(DouyinDiscoveryPageAnalyzer.Analyze(title, loginWall)?.Kind ==
              DouyinDiscoveryPageKind.LoginRequired,
            "a real QR login wall must take precedence over search results");
        Check(DouyinDiscoveryPageAnalyzer.Analyze(title, mixed)?.Kind !=
              DouyinDiscoveryPageKind.LoginRequired,
            "a header login button must not be mistaken for a login wall");

        var falseDomain = new[]
        {
            Control("Edit", "地址和搜索栏",
                "https://www.douyin.com.evil.example/user/fake"),
        };
        Check(DouyinDiscoveryPageAnalyzer.Analyze("Other - Google Chrome", falseDomain) is null,
            "a lookalike hostname must not be treated as a Douyin profile");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
