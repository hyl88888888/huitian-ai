namespace HuiTian.Core;

public interface IHardwareDetector
{
    Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default);
}
