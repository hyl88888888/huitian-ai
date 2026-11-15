using System.IO;
using System.Text.Json;

namespace HuiTian.App;

public sealed record CharacterOpeningOptions
{
    public string ImageFileName { get; init; } = "huitian-character-full-face-master.png";
    public double EyeAnchorX { get; init; } = .678;
    public double EyeAnchorY { get; init; } = .520;
    public double InitialScale { get; init; } = 2.35;
    public double DurationSeconds { get; init; } = 10;
    public bool ReduceMotion { get; init; }

    public static CharacterOpeningOptions Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16_384) return new();
            var value = JsonSerializer.Deserialize<CharacterOpeningOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value is not null && value.IsValid() ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ImageFileName) && ImageFileName == Path.GetFileName(ImageFileName) &&
        !ImageFileName.Contains(':') && ImageFileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        Path.GetExtension(ImageFileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" &&
        double.IsFinite(EyeAnchorX) && EyeAnchorX is >= 0 and <= 1 &&
        double.IsFinite(EyeAnchorY) && EyeAnchorY is >= 0 and <= 1 &&
        double.IsFinite(InitialScale) && InitialScale is >= 1 and <= 5 &&
        double.IsFinite(DurationSeconds) && DurationSeconds is >= 1 and <= 12;
}
