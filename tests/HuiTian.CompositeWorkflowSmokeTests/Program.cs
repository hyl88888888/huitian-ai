using System.Diagnostics;
using System.Text;
using HuiTian.Core;
using HuiTian.Infrastructure;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var progress = new Progress<CompositeWorkflowProgress>(item =>
    Console.WriteLine($"{item.Stage}: {item.Title} | {item.Detail}"));
var stopwatch = Stopwatch.StartNew();
var result = await new CompositeWorkflowFastPath().RunAsync(
    new CompositeTaskIntent("大理", "微信", "回天回归测试联系人"),
    progress,
    timeout.Token,
    deliver: false);
stopwatch.Stop();

if (result.Status != CompositeWorkflowStatus.Completed ||
    !result.ArtifactCreated ||
    result.ArtifactPath is null)
{
    throw new InvalidDataException($"Composite workflow failed: {result.Status} {result.Message}");
}

dynamic? powerPoint = null;
dynamic? presentation = null;
try
{
    var type = Type.GetTypeFromProgID("PowerPoint.Application")
               ?? throw new InvalidOperationException("PowerPoint is unavailable.");
    powerPoint = Activator.CreateInstance(type)!;
    presentation = powerPoint.Presentations.Open(result.ArtifactPath, WithWindow: false);
    if ((int)presentation.Slides.Count != 7)
    {
        throw new InvalidDataException("Expected a cover and six content slides.");
    }

    var weatherSlideText = ReadSlideText(presentation.Slides.Item(2));
    if (CountOccurrences(weatherSlideText, "预计降水") != 7)
    {
        throw new InvalidDataException(
            "The generated weather slide did not retain all seven daily forecasts.");
    }

    var sourceSlideText = ReadSlideText(presentation.Slides.Item(7));
    if (!sourceSlideText.Contains("MET Norway", StringComparison.Ordinal) ||
        !sourceSlideText.Contains("GeoNames", StringComparison.Ordinal) ||
        !sourceSlideText.Contains("CC BY 4.0", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The generated source slide is missing weather attribution.");
    }
}
finally
{
    try { presentation?.Close(); } catch { }
    try { powerPoint?.Quit(); } catch { }
}

Console.WriteLine(
    $"PASS: research -> real PPT in {stopwatch.Elapsed.TotalSeconds:0.0}s; delivery intentionally disabled; {result.ArtifactPath}");

static string ReadSlideText(dynamic slide)
{
    var builder = new StringBuilder();
    foreach (dynamic shape in slide.Shapes)
    {
        if (shape.HasTextFrame == -1 && shape.TextFrame.HasText == -1)
        {
            builder.AppendLine(Convert.ToString(shape.TextFrame.TextRange.Text));
        }
    }

    return builder.ToString();
}

static int CountOccurrences(string value, string needle)
{
    var count = 0;
    var offset = 0;
    while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += needle.Length;
    }

    return count;
}
