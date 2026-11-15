using System.Text.RegularExpressions;

namespace HuiTian.App;

/// <summary>
/// Pure policy for deciding when a streaming transcript is a complete user
/// turn.  Keeping this separate from the audio device makes the timing and
/// correction rules deterministic and testable.
/// </summary>
internal static partial class VoiceUtterancePolicy
{
    internal static readonly TimeSpan MaximumUtteranceDuration = TimeSpan.FromSeconds(30);

    private static readonly string[] IncompleteEndings =
    [
        "然后", "还有", "另外", "而且", "但是", "不过", "因为", "所以", "如果", "比如",
        "就是", "其实", "我想", "我希望", "我需要", "我想让你", "我希望你", "你能不能",
        "可不可以帮我", "请帮我", "帮我", "给我", "替我",
        "把", "给", "向", "跟", "和", "或者", "在", "去", "从", "关于", "对于",
        "不够", "还不够", "不是", "不要", "需要", "应该", "能不能", "可不可以",
    ];

    public static TimeSpan GetCommitDelay(string? transcript, TimeSpan elapsed)
    {
        var text = (transcript ?? string.Empty).Trim();
        if (elapsed >= MaximumUtteranceDuration)
        {
            return TimeSpan.FromMilliseconds(350);
        }

        var comparableLength = ComparableTextRegex().Replace(text, string.Empty).Length;
        if (IsLikelyIncomplete(text))
        {
            return CapToMaximum(TimeSpan.FromMilliseconds(3400), elapsed);
        }

        if (comparableLength <= 4)
        {
            return CapToMaximum(TimeSpan.FromMilliseconds(3000), elapsed);
        }

        if (HasExplicitSentenceEnding(text))
        {
            return CapToMaximum(TimeSpan.FromMilliseconds(1050), elapsed);
        }

        if (comparableLength <= 9)
        {
            return CapToMaximum(TimeSpan.FromMilliseconds(2350), elapsed);
        }

        if (comparableLength >= 18)
        {
            return CapToMaximum(TimeSpan.FromMilliseconds(1550), elapsed);
        }

        return CapToMaximum(TimeSpan.FromMilliseconds(1850), elapsed);
    }

    public static bool IsLikelyIncomplete(string? transcript)
    {
        var text = (transcript ?? string.Empty).Trim().TrimEnd('，', ',', '、', '；', ';', '：', ':');
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (HasExplicitSentenceEnding(text))
        {
            return false;
        }

        return IncompleteEndings.Any(ending =>
            text.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    public static string CorrectRecognition(string? value)
    {
        var corrected = (value ?? string.Empty).Trim();
        (string Wrong, string Right)[] commonCorrections =
        [
            ("有好小天", "你好小天"),
            ("友好小天", "你好小天"),
            ("又好小天", "你好小天"),
            ("你号小天", "你好小天"),
            ("泥好小天", "你好小天"),
            ("你好晓天", "你好小天"),
            ("你好小田", "你好小天"),
            ("回填回填", "回天回天"),
            ("灰天灰天", "回天回天"),
            ("回填", "回天"),
        ];
        foreach (var (wrong, right) in commonCorrections)
        {
            corrected = corrected.Replace(wrong, right, StringComparison.OrdinalIgnoreCase);
        }

        return corrected;
    }

    private static TimeSpan CapToMaximum(TimeSpan requested, TimeSpan elapsed)
    {
        var remaining = MaximumUtteranceDuration - elapsed;
        if (remaining <= TimeSpan.FromMilliseconds(350))
        {
            return TimeSpan.FromMilliseconds(350);
        }

        return requested <= remaining ? requested : remaining;
    }

    private static bool HasExplicitSentenceEnding(string text) =>
        text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？') ||
        text.EndsWith('!') || text.EndsWith('?');

    [GeneratedRegex(@"[\s\p{P}\p{S}]", RegexOptions.CultureInvariant)]
    private static partial Regex ComparableTextRegex();
}
