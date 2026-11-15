using HuiTian.Core;

namespace HuiTian.Infrastructure;

internal sealed record LocalBrainAsset(
    string AssetId,
    string FileName,
    long Length,
    string Sha256,
    IReadOnlyList<DownloadSource> Sources);

internal sealed record LocalBrainPackage(
    CapabilityTierId Tier,
    LocalBrainAsset Brain,
    LocalBrainAsset Vision,
    int ContextTokens,
    int PreferredGpuLayers);

internal static class LocalBrainCatalog
{
    private const string MainlandMirrorRoot =
        "https://hf-mirror.com/JonathanColetti/Qwen3.8-27B-Uncensored-GGUF/resolve/main/";
    private const string UpstreamRoot =
        "https://huggingface.co/JonathanColetti/Qwen3.8-27B-Uncensored-GGUF/resolve/main/";

    private static readonly LocalBrainAsset Vision = CreateAsset(
        "vision-f16",
        "Qwen3.8-27B-Uncensored-vision-f16.gguf",
        927_606_912,
        "5ac423f8a29059dc24e51bc6a43e9380dcd57a9347f28b62591e0b3f60b7081c");

    public static LocalBrainPackage ForTier(CapabilityTierId tier) => tier switch
    {
        CapabilityTierId.Huang => new(
            tier,
            CreateAsset(
                "brain-yellow",
                "Qwen3.8-27B-Uncensored-IQ2_M.gguf",
                10_624_771_968,
                "28e0f88eea09438220a086c2a1e5180ad83764c748856a28fd63ce1c0fbef187"),
            Vision,
            8_192,
            24),
        CapabilityTierId.Xuan => new(
            tier,
            CreateAsset(
                "brain-xuan",
                "Qwen3.8-27B-Uncensored-Q4_K_M.gguf",
                16_810_714_528,
                "4c5e2db039e9325ac7724c8846c71356a24ad1cdfa28002d73ecb6be645f9675"),
            Vision,
            16_384,
            40),
        CapabilityTierId.Di => new(
            tier,
            CreateAsset(
                "brain-di",
                "Qwen3.8-27B-Uncensored-Q6_K.gguf",
                22_430_999_968,
                "a50aa1478295b58ee3d93eabe02c17f6d5fcf6cb787fd8a0ab07ac629a46cae6"),
            Vision,
            32_768,
            99),
        CapabilityTierId.Tian => new(
            tier,
            CreateAsset(
                "brain-tian",
                "Qwen3.8-27B-Uncensored-Q8_0.gguf",
                29_047_084_448,
                "fb2cb9aa5407264aec6698faf41183f337809352abe0bd8fb750c0b689f4ced2"),
            Vision,
            65_536,
            99),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    private static LocalBrainAsset CreateAsset(
        string assetId,
        string fileName,
        long length,
        string sha256) =>
        new(
            assetId,
            fileName,
            length,
            sha256,
            [
                new DownloadSource("国内免费镜像", new Uri(MainlandMirrorRoot + fileName)),
                new DownloadSource("备用下载源", new Uri(UpstreamRoot + fileName)),
            ]);
}
