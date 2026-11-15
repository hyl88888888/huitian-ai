namespace HuiTian.Core;

public enum CapabilityTierId
{
    Huang = 0,
    Xuan = 1,
    Di = 2,
    Tian = 3,
}

public sealed record CapabilityTier(
    CapabilityTierId Id,
    string Name,
    string Speed,
    string Precision,
    string Context,
    string TaskScale,
    string Summary,
    string RecommendedHardware);

public static class CapabilityTiers
{
    public static IReadOnlyList<CapabilityTier> All { get; } =
    [
        new(
            CapabilityTierId.Huang,
            "黄阶·初启",
            "标准响应",
            "稳定理解",
            "日常上下文",
            "日常任务",
            "完整能力，适合日常使用",
            "16 GB 以上内存｜6–8 GB 级显存｜20 GB 以上可用空间"),
        new(
            CapabilityTierId.Xuan,
            "玄阶·通明",
            "更快响应",
            "更稳理解",
            "更长上下文",
            "多步骤任务",
            "更快看懂并完成多步骤工作",
            "24 GB 以上内存｜12–16 GB 级显存｜24 GB 以上可用空间"),
        new(
            CapabilityTierId.Di,
            "地阶·纵横",
            "高速响应",
            "更高精度",
            "长上下文",
            "复杂长任务",
            "适合复杂任务与持续操作",
            "32–64 GB 内存｜20–24 GB 级显存｜32 GB 以上可用空间"),
        new(
            CapabilityTierId.Tian,
            "天阶·无极",
            "极速响应",
            "最高精度",
            "超长上下文",
            "高强度多任务",
            "面向高强度、多任务与长期运行",
            "64 GB 以上内存｜32 GB 级显存及以上｜40 GB 以上可用空间"),
    ];

    public static CapabilityTier Get(CapabilityTierId id) =>
        All.First(tier => tier.Id == id);
}
