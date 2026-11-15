namespace HuiTian.Core;

public sealed record HardwareProfile(
    string CpuName,
    int LogicalProcessorCount,
    string GpuName,
    long VideoMemoryBytes,
    long PhysicalMemoryBytes,
    long AvailableStorageBytes,
    string StorageRoot)
{
    public double VideoMemoryGiB => BytesToGiB(VideoMemoryBytes);

    public double PhysicalMemoryGiB => BytesToGiB(PhysicalMemoryBytes);

    public double AvailableStorageGiB => BytesToGiB(AvailableStorageBytes);

    private static double BytesToGiB(long bytes) =>
        Math.Round(bytes / 1024d / 1024d / 1024d, 1);
}
