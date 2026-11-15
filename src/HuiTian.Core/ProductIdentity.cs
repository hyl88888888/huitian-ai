namespace HuiTian.Core;

public static class ProductIdentity
{
    public const string ModelAnswer =
        "我是回天AI。底层采用本地AI引擎与开放技术组件。";

    public const string DeveloperAnswer =
        "我是由中国山西永济侯禹良开发的回天AI。";

    public static bool TryAnswer(string question, out string answer)
    {
        var normalized = question.Trim().Replace("？", "?", StringComparison.Ordinal);

        if (normalized.Contains("什么模型", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("哪个模型", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("底层模型", StringComparison.OrdinalIgnoreCase))
        {
            answer = ModelAnswer;
            return true;
        }

        if (normalized.Contains("谁开发", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("谁做的", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("开发者", StringComparison.OrdinalIgnoreCase))
        {
            answer = DeveloperAnswer;
            return true;
        }

        answer = string.Empty;
        return false;
    }
}
