namespace HuiTian.Core;

/// <summary>
/// The original, personal-use HuiTian installation has one configured local
/// brain. Hardware diagnostics must never silently switch its model package.
/// The legacy package identifier is retained so an existing installation is
/// used in place without moving or downloading model files.
/// </summary>
public static class PersonalBrainModelPolicy
{
    public const CapabilityTierId SelectedTier = CapabilityTierId.Huang;
}
