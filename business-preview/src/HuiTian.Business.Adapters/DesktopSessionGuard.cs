using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HuiTian.Business.Adapters;

/// <summary>One Windows logon session shares a writer mutex and durable uncertainty marker.</summary>
internal sealed class DesktopSessionGuard
{
    private static readonly ConcurrentDictionary<string,DesktopSessionGuard> Guards=new(StringComparer.Ordinal);
    private readonly Mutex writer;
    private readonly EventWaitHandle blocked;
    private readonly string marker;
    internal string Scope { get; }
    private DesktopSessionGuard(string scope,string directory)
    {
        Scope=scope;Directory.CreateDirectory(directory);
        if(FilePolicy.HasReparsePoint(directory))throw new InvalidOperationException("桌面安全状态目录不能为链接。");
        marker=Path.Combine(directory,scope+".blocked.json");
        writer=new Mutex(false,@"Local\HuiTianBusiness.DesktopWriter."+scope);
        blocked=new EventWaitHandle(File.Exists(marker),EventResetMode.ManualReset,@"Local\HuiTianBusiness.DesktopBlocked."+scope);
        if(File.Exists(marker))blocked.Set();
    }
    internal static DesktopSessionGuard Get(string? testIsolationKey)
    {
        string scope,directory;
        if(testIsolationKey is not null)
        {
            if(!Guid.TryParseExact(testIsolationKey,"N",out _))throw new ArgumentException("测试隔离标识必须为GUID。");
            scope="Test-"+testIsolationKey;directory=Path.Combine(@"D:\HuiTianBusiness\Cache\Tests","DesktopSafety",testIsolationKey);
        }
        else
        {
            scope="Session-"+Process.GetCurrentProcess().SessionId+"-Logon-"+LogonId();
            directory=Path.Combine(BusinessPathsForGuard(),"DesktopSafety");
        }
        return Guards.GetOrAdd(scope,_=>new(scope,directory));
    }
    private static string BusinessPathsForGuard()=>HuiTian.Business.Core.BusinessPaths.Discover().Data;
    internal bool IsBlocked=>blocked.WaitOne(0)||File.Exists(marker);
    internal bool TryEnter(out string reason)
    {
        if(IsBlocked){reason="本Windows登录会话的桌面写入已因先前超时或结果不确定而隔离；不能通过新任务或进程重试。";return false;}
        bool entered;
        try{entered=writer.WaitOne(0);}
        catch(AbandonedMutexException)
        {MarkUncertain("Previous desktop writer exited without releasing its operation.");writer.ReleaseMutex();reason="先前桌面执行进程异常退出，操作结果不确定，已隔离写入。";return false;}
        if(!entered){reason="另一任务仍持有本Windows会话的桌面写入权；当前操作未提交。";return false;}
        if(IsBlocked){writer.ReleaseMutex();reason="桌面写入已隔离，当前操作未提交。";return false;}
        reason="";return true;
    }
    internal void Exit()=>writer.ReleaseMutex();
    internal void MarkUncertain(string reason)
    {
        blocked.Set();
        try
        {
            using var file=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            JsonSerializer.Serialize(file,new{scope=Scope,utc=DateTimeOffset.UtcNow,reason,processId=Environment.ProcessId,noAutomaticRetry=true,recovery="Requires manual review; do not clear automatically or start a replacement worker."});
            file.Flush(true);
        }
        catch(IOException) when(File.Exists(marker)){ }
        catch(UnauthorizedAccessException){ /* The named event remains blocked for every live process. */ }
    }
    private static string LogonId()
    {
        using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        _=GetTokenInformation(identity.AccessToken,10,0,0,out int needed);
        if(needed<16)throw new InvalidOperationException("无法取得Windows登录会话标识，桌面写入不可启用。");
        var buffer=Marshal.AllocHGlobal(needed);
        try
        {
            if(!GetTokenInformation(identity.AccessToken,10,buffer,needed,out _))throw new InvalidOperationException("无法核实Windows登录会话，桌面写入不可启用。");
            uint low=unchecked((uint)Marshal.ReadInt32(buffer,8));uint high=unchecked((uint)Marshal.ReadInt32(buffer,12));
            return high.ToString("x8")+low.ToString("x8");
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,nint information,int length,out int returnLength);
}
