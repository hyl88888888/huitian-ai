using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Security.Cryptography;
using HuiTian.Business.Core;

namespace HuiTian.Business.Adapters;

/// <summary>
/// Native Windows/UIA primitive adapter. It accepts only recently observed identities.
/// A timed-out provider is quarantined: no new work, retry, or replacement worker is started.
/// A provider call already entered cannot safely be aborted; this uncertainty is reported.
/// </summary>
internal sealed class WindowsDesktopAdapter : IAsyncDisposable
{
    private sealed record WindowRef(string Id,nint Handle,uint ProcessId,long StartTicks,string Title,string Class,DateTimeOffset Observed);
    private sealed record ElementRef(string Id,AutomationElement Element,int[] RuntimeId,WindowRef Window,string Name,string AutomationId,int ControlType,DateTimeOffset Observed);
    private sealed record Work(Func<Operation,ToolResult> Action,Operation Operation,TaskCompletionSource<ToolResult> Completion,bool ExclusiveWrite);
    private sealed record Approval(string ElementId,string Action,string ArgumentHash,string ContextHash,DateTimeOffset Prepared);
    private sealed class Operation(CancellationToken cancellationToken)
    {
        internal CancellationToken Token { get; }=cancellationToken;
        private int actionStarted;
        internal string TargetEvidence { get; set; }="";
        internal bool ActionStarted=>Volatile.Read(ref actionStarted)!=0;
        internal void BeforeAction(){Token.ThrowIfCancellationRequested();Interlocked.Exchange(ref actionStarted,1);}
    }
    private readonly BlockingCollection<Work> queue=new(1);
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Thread worker;
    private readonly TimeSpan timeout;
    private readonly TimeSpan windowLifetime;
    private readonly TimeSpan elementLifetime;
    private readonly DesktopSessionGuard sessionGuard;
    private readonly Dictionary<string,WindowRef> windows=new(StringComparer.Ordinal);
    private readonly Dictionary<string,ElementRef> elements=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Approval> approvals=new(StringComparer.Ordinal);
    private int quarantined;
    private int disposed;
    internal WindowsDesktopAdapter(TimeSpan? timeout=null,TimeSpan? elementLifetime=null,string? testIsolationKey=null)
    {
        this.timeout=timeout??TimeSpan.FromSeconds(5);
        this.elementLifetime=elementLifetime??TimeSpan.FromSeconds(45);
        windowLifetime=TimeSpan.FromMinutes(2);
        sessionGuard=DesktopSessionGuard.Get(testIsolationKey);
        worker=new Thread(WorkerLoop){IsBackground=true,Name="HuiTian isolated UI Automation worker"};
        worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    internal Task<ToolResult> ListWindowsAsync(int maxItems,CancellationToken ct)=>RunAsync(op=>
    {
        if(maxItems is <1 or >100)throw new ArgumentException("窗口列表上限必须在1至100之间。");
        windows.Clear();elements.Clear();approvals.Clear();var records=new List<object>();int scanned=0;bool truncated=false;
        Native.EnumWindows((handle,_)=>
        {
            if(op.Token.IsCancellationRequested)return false;
            if(++scanned>500||records.Count>=maxItems){truncated=true;return false;}
            if(!Native.IsWindowVisible(handle))return true;
            var title=WindowTitle(handle);if(string.IsNullOrWhiteSpace(title))return true;
            Native.GetWindowThreadProcessId(handle,out var pid);if(pid==0)return true;
            var className=new StringBuilder(256);Native.GetClassName(handle,className,className.Capacity);
            long ticks;string processName;
            try{using var process=Process.GetProcessById(checked((int)pid));ticks=process.StartTime.ToUniversalTime().Ticks;processName=process.ProcessName;}
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return true;}
            var id="window_"+Guid.NewGuid().ToString("N");var entry=new WindowRef(id,handle,pid,ticks,title,className.ToString(),DateTimeOffset.UtcNow);windows[id]=entry;
            records.Add(new{windowId=id,title,processId=pid,processName,windowClass=entry.Class,minimized=Native.IsIconic(handle),foreground=Native.GetForegroundWindow()==handle});
            return true;
        },0);
        op.Token.ThrowIfCancellationRequested();
        return Result(true,$"观察到{records.Count}个可见顶层窗口。",new{windows=records,truncated,maximum=maxItems,windowIdsValidForSeconds=(int)windowLifetime.TotalSeconds,untrustedWindowContent=true,scope="Visible top-level Windows windows and dialogs; protected/system surfaces may be omitted"});
    },ct);
    internal Task<ToolResult> ActivateAsync(string windowId,CancellationToken ct)=>RunAsync(op=>
    {
        var window=GetWindow(windowId,op);op.TargetEvidence=JsonSerializer.Serialize(new{action="activate",windowId,window.Title,window.ProcessId});op.BeforeAction();
        if(Native.IsIconic(window.Handle))Native.ShowWindowAsync(window.Handle,9);
        var requested=Native.SetForegroundWindow(window.Handle);var verified=Native.GetForegroundWindow()==window.Handle;
        return Result(verified,verified?"已核实目标窗口成为前台窗口。":"Windows未允许将目标窗口置于前台；请手动激活后继续。",new{windowId,activationRequested=requested,foregroundVerified=verified,businessOutcomeVerified=verified},businessOutcomeVerified:verified);
    },ct,exclusiveWrite:true);
    internal Task<ToolResult> ReadWindowAsync(string windowId,int maxNodes,int maxDepth,CancellationToken ct)=>RunAsync(op=>
    {
        if(maxNodes is <1 or >400||maxDepth is <0 or >12)throw new ArgumentException("控件树最多400个节点、12层深度。");
        var window=GetWindow(windowId,op);elements.Clear();approvals.Clear();
        var cache=new CacheRequest{TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full};
        foreach(var property in new[]{AutomationElement.NameProperty,AutomationElement.AutomationIdProperty,AutomationElement.ControlTypeProperty,AutomationElement.ProcessIdProperty,AutomationElement.IsPasswordProperty,AutomationElement.IsEnabledProperty,AutomationElement.IsOffscreenProperty,AutomationElement.IsInvokePatternAvailableProperty,AutomationElement.IsSelectionItemPatternAvailableProperty,AutomationElement.IsValuePatternAvailableProperty})cache.Add(property);
        var root=AutomationElement.FromHandle(window.Handle).GetUpdatedCache(cache);
        var pending=new Queue<(AutomationElement Element,string? Parent,int Depth)>();pending.Enqueue((root,null,0));
        var nodes=new List<object>();var walker=TreeWalker.ControlViewWalker;bool truncated=false;int skipped=0;
        while(pending.Count>0&&nodes.Count<maxNodes)
        {
            op.Token.ThrowIfCancellationRequested();var node=pending.Dequeue();
            try
            {
                var info=node.Element.Cached;
                if(info.ProcessId!=(int)window.ProcessId){skipped++;continue;}
                var protectedInput=info.IsPassword||SensitiveName(info.Name,info.AutomationId);
                var id="control_"+Guid.NewGuid().ToString("N");
                var entry=new ElementRef(id,node.Element,node.Element.GetRuntimeId(),window,info.Name,info.AutomationId,info.ControlType.Id,DateTimeOffset.UtcNow);
                elements[id]=entry;
                bool Can(AutomationProperty property)=>node.Element.GetCachedPropertyValue(property) is true;
                var actions=new List<string>();if(!protectedInput&&info.IsEnabled&&!info.IsOffscreen)
                {if(Can(AutomationElement.IsInvokePatternAvailableProperty))actions.Add("invoke");if(Can(AutomationElement.IsSelectionItemPatternAvailableProperty))actions.Add("select");if(info.ControlType==ControlType.Edit&&Can(AutomationElement.IsValuePatternAvailableProperty))actions.Add("fill");}
                string? value=null;
                if(!protectedInput&&info.ControlType==ControlType.Edit&&Can(AutomationElement.IsValuePatternAvailableProperty)&&node.Element.TryGetCurrentPattern(ValuePattern.Pattern,out var valuePattern))value=Clip(((ValuePattern)valuePattern).Current.Value,2000);
                nodes.Add(new{elementId=id,parentId=node.Parent,depth=node.Depth,name=protectedInput?"[受保护输入]":Clip(info.Name,300),automationId=Clip(info.AutomationId,120),controlType=info.ControlType.ProgrammaticName.Replace("ControlType.",""),enabled=info.IsEnabled,offscreen=info.IsOffscreen,passwordOrSensitive=protectedInput,value,actions});
                if(node.Depth>=maxDepth){if(walker.GetFirstChild(node.Element,cache) is not null)truncated=true;continue;}
                var child=walker.GetFirstChild(node.Element,cache);
                while(child is not null)
                {
                    op.Token.ThrowIfCancellationRequested();if(nodes.Count+pending.Count>=maxNodes){truncated=true;break;}
                    pending.Enqueue((child,id,node.Depth+1));child=walker.GetNextSibling(child,cache);
                }
            }
            catch(ElementNotAvailableException){skipped++;}
        }
        return Result(true,$"已观察窗口“{window.Title}”中的{nodes.Count}个UI Automation控件。",new{windowId,windowTitle=window.Title,nodes,truncated=truncated||pending.Count>0,skippedUnavailable=skipped,elementIdsValidForSeconds=(int)elementLifetime.TotalSeconds,untrustedWindowContent=true,limitations="Only UIA-exposed controls in this process/window; owner-drawn, remote, elevated and protected surfaces may be unavailable. No pixel inference."});
    },ct);
    internal async Task<string> DescribeApprovalAsync(string elementId,string action,string value,CancellationToken ct)
    {
        var result=await RunAsync(op=>
        {
            if(sessionGuard.IsBlocked)throw new InvalidOperationException("本Windows登录会话的桌面写入已隔离，不可发起新的操作审批。");
            if(action is not ("invoke" or "select" or "fill"))throw new ArgumentException("未知的桌面操作。");
            var entry=GetElement(elementId,op);var context=CaptureContext(entry.Window,op);
            approvals[elementId]=new(elementId,action,Hash(value),context.Hash,DateTimeOffset.UtcNow);
            string processName;using(var process=Process.GetProcessById((int)entry.Window.ProcessId))processName=process.ProcessName;
            var text=$"真实桌面目标（来自Windows/UI Automation，非模型标题）：\n程序：{processName}，PID {entry.Window.ProcessId}\n窗口：{entry.Window.Title}\n控件：{entry.Name}；AutomationId={entry.AutomationId}；类型={entry.ControlType}\n动作：{action}\n"+
                (action=="fill"?$"将填写：{value}\n":"未知控件可能具有发送、删除、支付等副作用，必须由人工确认该真实目标。\n")+
                "当前窗口上下文（不含密码值；焦点变化不算上下文变化）：\n"+context.Display+"\n执行前将再次匹配上下文指纹；变化即拒绝。";
            if(text.Length>14000)throw new InvalidOperationException("真实窗口上下文过长，无法完整展示供审批；当前通用操作已拒绝。");
            return Result(true,text,new{elementId,windowId=entry.Window.Id,contextHash=context.Hash,contextSource="Windows UI Automation",prepared=true});
        },ct);
        if(!result.Success)throw new InvalidOperationException(result.Summary);return result.Summary;
    }
    internal Task<ToolResult> InvokeAsync(string elementId,string action,CancellationToken ct)=>RunAsync(op=>
    {
        if(action is not ("invoke" or "select"))throw new ArgumentException("控件动作只允许invoke或select。");
        var entry=GetElement(elementId,op);VerifyApproval(entry,action,"",op);op.TargetEvidence=JsonSerializer.Serialize(new{action,elementId,windowId=entry.Window.Id,entry.Window.Title,control=entry.Name});var element=entry.Element;bool? selectionVerified=null;
        if(action=="invoke")
        {
            if(!element.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern))throw new ArgumentException("该控件未公开Invoke模式，不能猜测点击。");
            op.BeforeAction();((InvokePattern)pattern).Invoke();
        }
        else
        {
            if(!element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var pattern))throw new ArgumentException("该控件未公开SelectionItem模式。");
            op.BeforeAction();((SelectionItemPattern)pattern).Select();op.Token.ThrowIfCancellationRequested();selectionVerified=((SelectionItemPattern)pattern).Current.IsSelected;
        }
        elements.Clear();approvals.Clear();
        return Result(true,action=="invoke"?"已向观察到的控件提交Invoke请求；业务结果尚未验证，请重新读取窗口。":"已提交选择请求；选择状态已回读，业务结果仍须重新观察。",new{elementId,windowId=entry.Window.Id,action,actionSubmitted=true,selectionVerified,businessOutcomeVerified=false,requiresObservation=true,noAutomaticRetry=true},businessOutcomeVerified:false);
    },ct,exclusiveWrite:true);
    internal Task<ToolResult> FillAsync(string elementId,string value,CancellationToken ct)=>RunAsync(op=>
    {
        if(value.Length>10000)throw new ArgumentException("单次控件填写不能超过10000字符。");
        var entry=GetElement(elementId,op);VerifyApproval(entry,"fill",value,op);op.TargetEvidence=JsonSerializer.Serialize(new{action="fill",elementId,windowId=entry.Window.Id,entry.Window.Title,control=entry.Name,argumentHash=Hash(value)});var element=entry.Element;
        if(element.Current.ControlType!=ControlType.Edit)throw new ArgumentException("只允许填写非密码Edit控件。");
        if(!element.TryGetCurrentPattern(ValuePattern.Pattern,out var available))throw new ArgumentException("该编辑框没有公开可写Value模式。");
        var pattern=(ValuePattern)available;if(pattern.Current.IsReadOnly)throw new ArgumentException("编辑框为只读。");
        op.BeforeAction();pattern.SetValue(value);op.Token.ThrowIfCancellationRequested();var actual=pattern.Current.Value;
        bool verified=string.Equals(actual,value,StringComparison.Ordinal);elements.Clear();approvals.Clear();
        return Result(verified,verified?"已填写控件，并回读确认文字一致；相关业务是否完成须继续观察。":"已提交填写，但回读文字不一致；不能确认完成，请人工查看。",new{elementId,windowId=entry.Window.Id,action="fill",actionSubmitted=true,valueVerified=verified,readback=Clip(actual,2000),readbackTruncated=actual.Length>2000,businessOutcomeVerified=false,requiresObservation=true,noAutomaticRetry=true},needsHuman:!verified,businessOutcomeVerified:false);
    },ct,exclusiveWrite:true);
    private void VerifyApproval(ElementRef entry,string action,string value,Operation op)
    {
        if(!approvals.Remove(entry.Id,out var approved)||approved.Action!=action||approved.ArgumentHash!=Hash(value)||DateTimeOffset.UtcNow-approved.Prepared>elementLifetime)throw new ArgumentException("没有与当前目标和参数匹配的有效审批上下文；请重新观察并确认。");
        var context=CaptureContext(entry.Window,op);
        if(context.Hash!=approved.ContextHash)throw new ArgumentException("批准后窗口业务上下文已变化；本次操作未提交，请重新观察并审批。");
        op.Token.ThrowIfCancellationRequested();
    }
    private (string Hash,string Display) CaptureContext(WindowRef window,Operation op)
    {
        var root=AutomationElement.FromHandle(window.Handle);var pending=new Queue<(AutomationElement Element,int Depth)>();pending.Enqueue((root,0));
        var nodes=new List<object>();var visible=new List<string>();var walker=TreeWalker.ControlViewWalker;
        while(pending.Count>0)
        {
            op.Token.ThrowIfCancellationRequested();if(nodes.Count>=400)throw new ArgumentException("窗口上下文超过400控件，无法完整核验，当前通用写入已拒绝。");
            var (element,depth)=pending.Dequeue();var info=element.Current;
            if(info.ProcessId!=(int)window.ProcessId)throw new ArgumentException("窗口包含跨进程控件，当前无法完整核验业务上下文。");
            bool sensitive=info.IsPassword||SensitiveName(info.Name,info.AutomationId);string value="";bool? selected=null;
            if(!sensitive&&element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern))value=((ValuePattern)pattern).Current.Value;
            else if(!sensitive&&info.ControlType==ControlType.Document&&element.TryGetCurrentPattern(TextPattern.Pattern,out var text))value=((TextPattern)text).DocumentRange.GetText(10001);
            if(value.Length>10000)throw new ArgumentException("控件文字过长，无法完整展示审批上下文。");
            if(element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var selection))selected=((SelectionItemPattern)selection).Current.IsSelected;
            nodes.Add(new{depth,runtime=element.GetRuntimeId(),name=sensitive?"[protected]":info.Name,info.AutomationId,type=info.ControlType.Id,info.IsEnabled,info.IsOffscreen,sensitive,value,selected});
            if(!info.IsOffscreen)visible.Add($"{info.ControlType.ProgrammaticName.Replace("ControlType.","")}: {(sensitive?"[受保护输入]":info.Name)}"+(string.IsNullOrEmpty(value)?"":"；值="+value)+(selected.HasValue?"；已选择="+selected:""));
            var child=walker.GetFirstChild(element);
            if(child is not null&&depth>=12)throw new ArgumentException("窗口上下文超过12层，无法完整核验，当前通用写入已拒绝。");
            while(child is not null){if(nodes.Count+pending.Count>=400)throw new ArgumentException("窗口上下文超过400控件，当前通用写入已拒绝。");pending.Enqueue((child,depth+1));child=walker.GetNextSibling(child);}
        }
        var display=string.Join("\n",visible);if(display.Length>12000)throw new ArgumentException("真实窗口业务上下文过长，无法完整供人工审核。");
        return(Hash(JsonSerializer.Serialize(new{window.ProcessId,window.StartTicks,window.Title,window.Class,nodes})),display);
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private WindowRef GetWindow(string id,Operation op)
    {
        op.Token.ThrowIfCancellationRequested();
        if(!windows.TryGetValue(id,out var window))throw new ArgumentException("windowId不是当前窗口观察返回的编号，请先列出窗口。");
        if(DateTimeOffset.UtcNow-window.Observed>windowLifetime)throw new ArgumentException("窗口编号已过期，请重新列出窗口。");
        if(!Native.IsWindow(window.Handle)||!Native.IsWindowVisible(window.Handle))throw new ArgumentException("目标窗口已关闭或不可见。");
        Native.GetWindowThreadProcessId(window.Handle,out var pid);if(pid!=window.ProcessId)throw new ArgumentException("窗口身份已变化，请重新观察。");
        using var process=Process.GetProcessById(checked((int)pid));if(process.StartTime.ToUniversalTime().Ticks!=window.StartTicks)throw new ArgumentException("目标进程已重启，请重新观察。");
        var title=WindowTitle(window.Handle);op.Token.ThrowIfCancellationRequested();if(title!=window.Title)throw new ArgumentException("窗口标题已变化，请重新列出窗口。");
        return window;
    }
    private ElementRef GetElement(string id,Operation op)
    {
        if(!elements.TryGetValue(id,out var entry))throw new ArgumentException("elementId不是最近控件快照返回的编号，请重新读取窗口。");
        if(DateTimeOffset.UtcNow-entry.Observed>elementLifetime)throw new ArgumentException("控件编号已过期，请重新读取窗口。");
        var window=GetWindow(entry.Window.Id,op);var info=entry.Element.Current;
        if(info.IsPassword||SensitiveName(info.Name,info.AutomationId))throw new ArgumentException("密码、凭据与验证码控件必须人工操作。");
        if(info.ProcessId!=(int)window.ProcessId||info.ControlType.Id!=entry.ControlType||info.Name!=entry.Name||info.AutomationId!=entry.AutomationId||!entry.Element.GetRuntimeId().SequenceEqual(entry.RuntimeId))throw new ArgumentException("控件身份或内容已变化，请重新观察。");
        if(!info.IsEnabled||info.IsOffscreen)throw new ArgumentException("控件已禁用或离屏，不能执行。");
        var ancestor=entry.Element;bool withinWindow=false;
        for(int depth=0;ancestor is not null&&depth<32;depth++)
        {op.Token.ThrowIfCancellationRequested();if(ancestor.Current.NativeWindowHandle==(long)window.Handle){withinWindow=true;break;}ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor);}
        if(!withinWindow)throw new ArgumentException("控件已离开原窗口，请重新观察。");
        op.Token.ThrowIfCancellationRequested();return entry;
    }
    private async Task<ToolResult> RunAsync(Func<Operation,ToolResult> action,CancellationToken ct,bool exclusiveWrite=false)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(Volatile.Read(ref disposed)!=0)return Result(false,"桌面观察会话已关闭。",new{disposed=true});
            if(Volatile.Read(ref quarantined)!=0)return Result(false,"先前UI Automation调用超时，此会话已隔离；不会再次操作。请人工确认目标程序状态并重新启动任务。",new{sessionQuarantined=true,noAutomaticRetry=true},true);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);deadline.CancelAfter(timeout);
            var operation=new Operation(deadline.Token);var completion=new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(new(action,operation,completion,exclusiveWrite),ct);
            try{return await completion.Task.WaitAsync(timeout,ct);}
            catch(Exception ex) when(ex is TimeoutException||ex is OperationCanceledException)
            {
                Interlocked.Exchange(ref quarantined,1);deadline.Cancel();sessionGuard.MarkUncertain(operation.ActionStarted?"Desktop call timed out or was cancelled after submission.":"Desktop provider timed out or was cancelled while an operation was pending.");
                if(operation.ActionStarted)throw new ActionOutcomeUncertainException("已提交桌面操作后收到取消或超时；业务结果不确定，桌面写入已隔离，禁止自动重试。",JsonSerializer.Serialize(new{actionSubmitted=true,actionMayStillComplete=true,businessOutcomeVerified=false,noAutomaticRetry=true,sessionQuarantined=true,scope=sessionGuard.Scope,target=operation.TargetEvidence}),ct);
                if(ct.IsCancellationRequested)throw new OperationCanceledException(ct);
                return Result(false,operation.ActionStarted?"控件服务超时。操作可能稍后完成，此会话已隔离且不会重复提交；请人工确认。":"控件服务超时，此会话已隔离；后续控件动作已禁止。",new{sessionQuarantined=true,timeoutMilliseconds=(int)timeout.TotalMilliseconds,actionMayStillComplete=operation.ActionStarted,businessOutcomeVerified=false,noAutomaticRetry=true},true);
            }
        }
        finally{gate.Release();}
    }
    private void WorkerLoop()
    {
        foreach(var work in queue.GetConsumingEnumerable())
        {
            bool ownsWriter=false;
            try
            {
                work.Operation.Token.ThrowIfCancellationRequested();
                if(work.ExclusiveWrite&&!sessionGuard.TryEnter(out var reason)){work.Completion.TrySetResult(Result(false,reason,new{writeNotSubmitted=true,sessionGuard=sessionGuard.Scope,sessionQuarantined=sessionGuard.IsBlocked,noAutomaticRetry=true},true));continue;}
                ownsWriter=work.ExclusiveWrite;work.Completion.TrySetResult(work.Action(work.Operation));
            }
            catch(OperationCanceledException)
            {
                if(work.Operation.ActionStarted){sessionGuard.MarkUncertain("Desktop worker cancelled after submission.");work.Completion.TrySetException(new ActionOutcomeUncertainException("桌面操作已提交但结果不确定，禁止自动重试。",JsonSerializer.Serialize(new{actionSubmitted=true,businessOutcomeVerified=false,noAutomaticRetry=true}),work.Operation.Token));}
                else work.Completion.TrySetCanceled(work.Operation.Token);
            }
            catch(Exception ex)
            {
                if(work.Operation.ActionStarted)sessionGuard.MarkUncertain("Desktop provider failed after action submission.");
                string message=ex switch{ElementNotAvailableException=>"目标控件已失效，请重新观察窗口。",ElementNotEnabledException=>"目标控件已禁用。",UnauthorizedAccessException=>"目标程序权限高于当前进程或禁止UI Automation访问。",COMException=>"目标程序没有响应可用的UI Automation接口。",ArgumentException or InvalidOperationException=>ex.Message,_=>"目标程序暂不支持此控件操作。"};
                work.Completion.TrySetResult(Result(false,message,new{errorCategory=ex.GetType().Name,actionMayHaveOccurred=work.Operation.ActionStarted,businessOutcomeVerified=false,noAutomaticRetry=work.Operation.ActionStarted},work.Operation.ActionStarted));
            }
            finally{if(ownsWriter)sessionGuard.Exit();}
        }
    }
    private static bool SensitiveName(string name,string automationId)=>Regex.IsMatch(name+" "+automationId,@"password|passcode|secret|credential|api[_ -]?key|验证码|密钥|密码|口令|\bpin\b",RegexOptions.IgnoreCase);
    private static string Clip(string? text,int length)=>string.IsNullOrEmpty(text)?"":text[..Math.Min(text.Length,length)];
    private static string WindowTitle(nint handle){var text=new StringBuilder(513);Native.GetWindowText(handle,text,text.Capacity);return text.ToString();}
    private static ToolResult Result(bool success,string summary,object evidence,bool needsHuman=false,bool? businessOutcomeVerified=null)=>new(success,summary,JsonSerializer.Serialize(evidence),NeedsHuman:needsHuman,BusinessOutcomeVerified:businessOutcomeVerified);
    public ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)==0){lifetime.Cancel();queue.CompleteAdding();}
        // Do not synchronously join an unresponsive third-party UIA provider or spawn a replacement.
        return ValueTask.CompletedTask;
    }
    private static class Native
    {
        internal delegate bool EnumWindowsProc(nint handle,nint parameter);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback,nint parameter);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint handle);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint handle);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint handle);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint handle,uint command);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle,out uint processId);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(nint handle,StringBuilder text,int count);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint handle,StringBuilder text,int count);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint handle);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint handle,int command);
    }
}
