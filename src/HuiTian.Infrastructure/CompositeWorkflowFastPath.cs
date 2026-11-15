using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public enum CompositeWorkflowStage
{
    Research,
    Presentation,
    Delivery,
    Completed,
}

public enum CompositeWorkflowStatus
{
    Completed,
    DeliveryNotCompleted,
    FastResearchUnavailable,
    Failed,
}

public sealed record CompositeWorkflowProgress(
    CompositeWorkflowStage Stage,
    string Title,
    string Detail);

public sealed record CompositeWorkflowResult(
    CompositeWorkflowStatus Status,
    string Message,
    string? ArtifactPath,
    long ElapsedMilliseconds,
    ChatFastPathResult? DeliveryResult = null)
{
    public bool ArtifactCreated =>
        !string.IsNullOrWhiteSpace(ArtifactPath) && File.Exists(ArtifactPath);
}

/// <summary>
/// Product-layer shortcut for the recurring commercial workflow
/// research -> travel outline -> PowerPoint -> chat delivery. It uses the
/// same local product tools as the agent skeleton, but avoids asking the 27B
/// brain to re-plan every deterministic step and avoids browser screenshots.
/// </summary>
public sealed partial class CompositeWorkflowFastPath
{
    private readonly WindowsToolBridge _tools = new();
    private readonly WindowsChatFastPath _chat = new();
    private readonly IWeatherForecastProvider _weather;

    public CompositeWorkflowFastPath(IWeatherForecastProvider? weather = null)
    {
        _weather = weather ?? new MetNorwayWeatherClient();
    }

    public async Task<CompositeWorkflowResult> RunAsync(
        CompositeTaskIntent intent,
        IProgress<CompositeWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool deliver = true)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            progress?.Report(new CompositeWorkflowProgress(
                CompositeWorkflowStage.Research,
                $"正在查询{intent.Location}的天气和旅行资料",
                "真实天气数据与旅行资料正在同时读取，不需要等待浏览器截图"));

            var weatherTask = _weather.GetSevenDayForecastAsync(
                intent.Location,
                cancellationToken);
            var travelTask = _tools.ExecuteAsync(
                "huitian_search_web",
                JsonSerializer.SerializeToElement(new
                {
                    query = $"{intent.Location}旅行攻略 交通 景点 住宿 注意事项",
                }),
                cancellationToken);
            await Task.WhenAll(weatherTask, travelTask).ConfigureAwait(false);
            var weather = await weatherTask.ConfigureAwait(false);
            var travel = await travelTask.ConfigureAwait(false);
            if (!weather.Success || weather.Forecast is null)
            {
                stopwatch.Stop();
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.FastResearchUnavailable,
                    weather.Message,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            if (!HasReadableResearch(travel))
            {
                stopwatch.Stop();
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.FastResearchUnavailable,
                    "旅行资料通道暂时没有返回可直接读取的摘要，任务将改用普通联网流程继续。",
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CompositeWorkflowProgress(
                CompositeWorkflowStage.Presentation,
                "正在生成旅行攻略 PPT",
                "已经取得资料，正在一次性排版成真实演示文稿"));

            var slides = BuildTravelSlides(intent.Location, weather.Forecast, travel.Text);
            var presentationArguments = JsonSerializer.SerializeToElement(new
            {
                title = $"{intent.Location}未来一周旅行攻略",
                slides = slides.Select(slide => new { title = slide.Title, body = slide.Body }).ToArray(),
            });
            var presentation = await Task.Run(
                    async () => await _tools.ExecuteAsync(
                        "huitian_create_presentation",
                        presentationArguments,
                        cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            var artifactPath = presentation.Success
                ? ExtractPresentationPath(presentation.Text)
                : null;
            if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
            {
                stopwatch.Stop();
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.Failed,
                    presentation.Success
                        ? "资料已整理，但演示文稿文件没有通过本机存在性核验。"
                        : "资料已整理，但演示文稿没有创建成功。",
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!deliver)
            {
                stopwatch.Stop();
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.Completed,
                    $"快速流水线诊断已完成，演示文稿已生成但按测试要求未发送。\n文件位置：{artifactPath}",
                    artifactPath,
                    stopwatch.ElapsedMilliseconds);
            }

            progress?.Report(new CompositeWorkflowProgress(
                CompositeWorkflowStage.Delivery,
                $"正在发送给{intent.Platform}联系人“{intent.Recipient}”",
                $"演示文稿已生成：{Path.GetFileName(artifactPath)}"));

            ChatFastPathResult delivery;
            try
            {
                using var deliveryBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deliveryBudget.CancelAfter(TimeSpan.FromSeconds(10));
                delivery = await _chat.SendAsync(
                        intent.Platform,
                        intent.Recipient,
                        message: null,
                        [artifactPath],
                        deliveryBudget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.DeliveryNotCompleted,
                    $"旅行攻略 PPT 已创建，但{intent.Platform}联系人核对超过了 10 秒，因此没有继续发送，也不会在后台重复操作。文件已保留：{artifactPath}",
                    artifactPath,
                    stopwatch.ElapsedMilliseconds);
            }

            stopwatch.Stop();
            if (!delivery.Success)
            {
                return new CompositeWorkflowResult(
                    CompositeWorkflowStatus.DeliveryNotCompleted,
                    $"旅行攻略 PPT 已创建，但没有完成向{intent.Platform}联系人“{intent.Recipient}”的发送。文件已保留，可直接打开或再次发送。",
                    artifactPath,
                    stopwatch.ElapsedMilliseconds,
                    delivery);
            }

            progress?.Report(new CompositeWorkflowProgress(
                CompositeWorkflowStage.Completed,
                "旅行攻略已制作并发送",
                $"已核验文件：{Path.GetFileName(artifactPath)}"));
            return new CompositeWorkflowResult(
                CompositeWorkflowStatus.Completed,
                $"已完成{intent.Location}天气资料整理、旅行攻略 PPT 制作，并发送给{intent.Platform}联系人“{intent.Recipient}”。\n文件位置：{artifactPath}",
                artifactPath,
                stopwatch.ElapsedMilliseconds,
                delivery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Composite fast path failed: {exception}");
            stopwatch.Stop();
            return new CompositeWorkflowResult(
                CompositeWorkflowStatus.Failed,
                "快速流水线暂时没有完成，任务会保留，且不会在后台重复发送。",
                null,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static bool HasReadableResearch(DynamicToolResult result) =>
        result.Success &&
        result.Text.Contains("已直接读取", StringComparison.Ordinal) &&
        result.Text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.StartsWith("摘要：", StringComparison.Ordinal) && line.Length >= 11);

    private static IReadOnlyList<(string Title, string Body)> BuildTravelSlides(
        string location,
        SevenDayWeatherForecast weather,
        string travelResearch)
    {
        var weatherPoints = weather.Days.Select(day => FormatDailyWeather(day)).ToArray();
        var travelPoints = ExtractResearchPoints(travelResearch, 10);
        var sourcePoints = new List<string>
        {
            "天气数据：MET Norway Locationforecast 2.0（NLOD 2.0 / CC BY 4.0）",
            $"预报接口：{weather.SourceUri}",
            "地点坐标：GeoNames cities15000（CC BY 4.0）：download.geonames.org/export/dump/",
            "许可链接：creativecommons.org/licenses/by/4.0/；data.norge.no/nlod/en/2.0",
            "改动说明：回天按中国标准时间汇总每日高低温、天气符号和降水量",
        };
        sourcePoints.AddRange(ExtractSourcePoints(string.Empty, travelResearch, 1));
        var localHighlights = location.Contains("大理", StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                "洱海环线：按天气在骑行、包车和分段游览之间切换",
                "苍山与索道：高海拔温差明显，遇大风或降雨及时调整",
                "大理古城、喜洲与周城：适合安排文化、美食和慢节奏体验",
                "时间充足可延伸至双廊或沙溪，避免一天塞入过多远距离点位",
            }
            : new[]
            {
                "优先选择同一区域内的景点组合，减少折返和通勤消耗",
                "把户外核心景点放在天气较稳定的时段，室内项目作为后备",
                "每天保留一段弹性时间，用于交通延误、休息或临时体验",
                "热门景点、交通和住宿在出发前再次核对营业及预约信息",
            };

        return
        [
            ("未来 7 日天气", JoinBullets(weatherPoints, null, maximum: 7)),
            ("行程设计原则", JoinBullets(
                [
                    "先确定抵达与离开时间，再按地理位置组合每日路线",
                    "晴朗时优先户外和远景，降雨时切换人文、美食与室内体验",
                    "控制每天一至两个核心目标，给用餐、交通和休息留出余量",
                    "高原、山区或昼夜温差大的目的地要准备分层穿衣",
                ], null)),
            ("推荐体验与动线", JoinBullets(localHighlights, null)),
            ("交通、住宿与预算", JoinBullets(travelPoints.Take(6),
                "住宿优先考虑交通便利、夜间安静和退改规则，不只比较最低价格。")),
            ("行前清单与风险预案", JoinBullets(
                [
                    "证件、充电设备、常用药、防晒和雨具分开放置，随身保留关键物品",
                    "提前保存车次、酒店、景点预约与紧急联系人信息",
                    "遇极端天气、道路管制或身体不适时立即缩短行程",
                    "付款、登录和实名预约由用户本人完成，注意核对官方渠道",
                ], null)),
            ("资料来源与使用说明", JoinBullets(sourcePoints,
                "天气会持续更新；价格与开放时间请在出发前通过官方渠道再次核对。")),
        ];
    }

    private static string FormatDailyWeather(DailyWeatherForecast day) =>
        $"{day.Date.ToString("M月d日 ddd", CultureInfo.GetCultureInfo("zh-CN"))}：" +
        $"{day.Condition}，{day.LowCelsius:0.#}～{day.HighCelsius:0.#}℃，" +
        $"预计降水 {day.PrecipitationMillimetres:0.#} 毫米";

    private static IReadOnlyList<string> ExtractResearchPoints(string text, int maximum)
    {
        var values = text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("摘要：", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .Where(line => line.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToList();
        return values;
    }

    private static IReadOnlyList<string> ExtractSourcePoints(
        string first,
        string second,
        int maximum)
    {
        return (first + "\n" + second).ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(line => line.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToList();
    }

    private static string JoinBullets(
        IEnumerable<string> points,
        string? fallback,
        int maximum = 6)
    {
        var values = points
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Select(point => "• " + Compact(point, 115))
            .Take(maximum)
            .ToList();
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
        {
            values.Add("• " + fallback);
        }
        else if (!string.IsNullOrWhiteSpace(fallback) && values.Count < maximum)
        {
            values.Add("• " + fallback);
        }

        return string.Join(Environment.NewLine, values);
    }

    private static string Compact(string value, int maximumLength)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= maximumLength
            ? compact
            : compact[..(maximumLength - 1)] + "…";
    }

    private static string? ExtractPresentationPath(string text)
    {
        var match = PresentationPathRegex().Match(text);
        return match.Success ? match.Value.Trim() : null;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]+?\.pptx", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresentationPathRegex();
}
