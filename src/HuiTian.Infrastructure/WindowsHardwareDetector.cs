using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed class WindowsHardwareDetector(HuiTianPaths paths) : IHardwareDetector
{
    public async Task<HardwareProfile> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();

        var cpuName = ReadCpuName();
        var physicalMemory = ReadPhysicalMemory();
        var (gpuName, videoMemory) = await ReadNvidiaGpuAsync(cancellationToken)
            .ConfigureAwait(false);
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root) ?? "D:\\");

        return new HardwareProfile(
            cpuName,
            Environment.ProcessorCount,
            gpuName,
            videoMemory,
            physicalMemory,
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            drive.Name);
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim()
                   ?? "Windows 处理器";
        }
        catch
        {
            return "Windows 处理器";
        }
    }

    private static long ReadPhysicalMemory()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? checked((long)status.TotalPhysical) : 0;
    }

    private static async Task<(string Name, long Bytes)> ReadNvidiaGpuAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            "nvidia-smi.exe",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            @"C:\Windows\System32\nvidia-smi.exe",
        };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments =
                            "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };

                if (!process.Start())
                {
                    continue;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var output = (await outputTask.ConfigureAwait(false))
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                var parts = output.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    long.TryParse(parts[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var mebibytes))
                {
                    return (parts[0], checked(mebibytes * 1024L * 1024L));
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception
                    or IOException)
            {
                // Try the next well-known location. Non-NVIDIA machines use the fallback.
            }
        }

        return ("兼容图形设备", 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
