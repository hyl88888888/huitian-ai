namespace HuiTian.Core;

/// <summary>Application/protocol identifiers, never natural-language intent matching.</summary>
public static class CommunicationPlatformIdentity
{
    public static string? Canonicalize(string? identifier) => identifier?.Trim().ToLowerInvariant() switch
    {
        "微信" or "wechat" or "weixin" => "微信",
        "qq" => "QQ",
        _ => null,
    };
}
