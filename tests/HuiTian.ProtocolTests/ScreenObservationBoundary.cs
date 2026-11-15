using HuiTian.Infrastructure;
using System.Text.Json;

internal static class ScreenObservationBoundary
{
    internal static void Run()
    {
        DouyinDiscoveryPageBoundary.Run();
        var seen = new ScreenObservationDeduplicator();
        var first = new DynamicToolResult(true, "first", "data:image/png;base64,AQID");
        if (!seen.Filter("Browser", first).Success)
            throw new Exception("first observation must remain visible");
        var repeated = seen.Filter("Browser", first);
        if (repeated.Success || repeated.ImageDataUrl is not null ||
            repeated.ResultCode != "observation_unchanged")
            throw new Exception("unchanged screenshot without action must not trigger a second vision pass");
        if (!seen.Filter("Browser", first with { ImageDataUrl = "data:image/png;base64,AQIE" }).Success)
            throw new Exception("a changed page must be observed normally");
        seen.MarkDesktopAction();
        if (!seen.Filter("Browser", first).Success)
            throw new Exception("a screenshot after desktop action must not be discarded");
        if (!seen.Filter("Other Browser", first).Success)
            throw new Exception("a different target window must not be discarded");
        if (seen.Filter("Browser", new DynamicToolResult(false, "unavailable",
                ResultCode: "observation_unavailable")).ResultCode != "observation_unavailable")
            throw new Exception("failed observation must preserve its real failure cause");

        var manyControls = Enumerable.Range(0, 200)
            .Select(index => new WindowsUiAutomation.InspectionControl(
                "Button", $"操作 {index}", $"control-{index}", true))
            .ToArray();
        var bounded = WindowsUiAutomation.FormatInspectionSummary("Browser", manyControls, 160);
        if (bounded.Length > 2_600 || ReadControlRows(bounded).Length > 36)
            throw new Exception("a large browser accessibility tree must fit the observation budget");
        var requestedOne = ReadControlRows(WindowsUiAutomation.FormatInspectionSummary(
            "Browser", manyControls, 1));
        if (requestedOne.Length != 1 ||
            requestedOne[0].GetProperty("elementName").GetString() != "操作 0")
            throw new Exception("inspection must honor a small requested item limit and preserve exact locators");

        var longLabel = new string('长', 500);
        var longId = new string('x', 200);
        var oversizedControls = new[]
        {
            new WindowsUiAutomation.InspectionControl("Text", longLabel, "", true),
            new WindowsUiAutomation.InspectionControl("Button", longLabel, "stable-id", true),
            new WindowsUiAutomation.InspectionControl("Button", "继续", longId, true),
        };
        var oversized = WindowsUiAutomation.FormatInspectionSummary("Browser", oversizedControls, 20);
        var oversizedRows = ReadControlRows(oversized);
        if (oversized.Length > 2_600 || oversized.Contains(longLabel, StringComparison.Ordinal) ||
            oversized.Contains(longId, StringComparison.Ordinal) || oversizedRows.Length != 2 ||
            oversizedRows[0].GetProperty("automationId").GetString() != "stable-id" ||
            oversizedRows[0].GetProperty("elementName").GetString() != "" ||
            oversizedRows[1].GetProperty("elementName").GetString() != "继续")
            throw new Exception("oversized names and IDs must never become misleading partial locators");

        var escapedControls = Enumerable.Range(0, 80)
            .Select(index => new WindowsUiAutomation.InspectionControl(
                "Button", new string('"', 100) + index, "", true));
        if (WindowsUiAutomation.FormatInspectionSummary("Browser", escapedControls, 80).Length > 2_600)
            throw new Exception("the character budget must include JSON escaping");
    }

    private static JsonElement[] ReadControlRows(string text) => text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith('{'))
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
        .ToArray();
}
