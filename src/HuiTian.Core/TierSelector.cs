namespace HuiTian.Core;

public static class TierSelector
{
    private const long GiB = 1024L * 1024L * 1024L;

    public static CapabilityTier Select(HardwareProfile hardware)
    {
        var memory = hardware.PhysicalMemoryBytes;
        var videoMemory = hardware.VideoMemoryBytes;

        if (memory >= 64 * GiB && videoMemory >= 32 * GiB)
        {
            return CapabilityTiers.Get(CapabilityTierId.Tian);
        }

        if (memory >= 32 * GiB && videoMemory >= 20 * GiB)
        {
            return CapabilityTiers.Get(CapabilityTierId.Di);
        }

        if (memory >= 24 * GiB && videoMemory >= 12 * GiB)
        {
            return CapabilityTiers.Get(CapabilityTierId.Xuan);
        }

        return CapabilityTiers.Get(CapabilityTierId.Huang);
    }
}
