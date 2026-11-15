using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using HuiTian.Business.Core;
using Microsoft.Playwright;

namespace HuiTian.Business.Adapters;

/// <summary>All browser HTTP traffic passes a pinned-IP public-address transport, including redirects.</summary>
internal sealed class BrowserAdapter : IAsyncDisposable
{
    private readonly BusinessPaths paths;
    private readonly bool headless;
    private readonly HttpClient transport;
    private readonly HttpClient publicDns;
    private readonly Dictionary<string,IElementHandle> elements=new(StringComparer.Ordinal);
    private sealed record BrowserApproval(string Tool,string Target,string Arguments,string Url,DateTimeOffset Prepared);
    private readonly Dictionary<string,BrowserApproval> approvals=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate=new(1,1);
    private IPlaywright? playwright;
    private IBrowserContext? context;
    private IPage? page;
    private bool allowWrite;
    private bool usedPublicDnsFallback;
    private int observation;
    private string observedUrl="";
    private readonly List<string> blockedRequests=[];
    private readonly ConcurrentQueue<string> networkEvents=new();
    internal BrowserAdapter(BusinessPaths paths,bool headless=false)
    {
        this.paths=paths;
        this.headless=headless;
        // A fixed public HTTPS endpoint avoids the machine's optional fake-IP DNS mapping.
        // This fallback is used only for 198.18.0.0/15, never for ordinary private DNS answers.
        publicDns=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(10)}){Timeout=TimeSpan.FromSeconds(12)};
        publicDns.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
        var handler=new SocketsHttpHandler {AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All,UseCookies=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(15)};
        handler.ConnectCallback=async (request,ct)=>
        {
            TraceNetwork("resolve: "+request.DnsEndPoint.Host);
            var addresses=await ResolvePublicAddressesAsync(request.DnsEndPoint.Host,ct);
            TraceNetwork("public DNS: "+string.Join(",",addresses.Select(a=>a.ToString())));
            Exception? last=null;
            foreach(var address in addresses)
            {
                var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(new IPEndPoint(address,request.DnsEndPoint.Port),ct);TraceNetwork("socket connected: "+address);return new NetworkStream(socket,ownsSocket:true);}
                catch(Exception ex){socket.Dispose();last=ex;}
            }
            throw new HttpRequestException("无法连接公开网站。",last);
        };
        transport=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(25)};
    }
    private async Task<IPAddress[]> ResolvePublicAddressesAsync(string host,CancellationToken ct)
    {
        var addresses=await Dns.GetHostAddressesAsync(host,ct);
        if(addresses.Length>0&&addresses.All(BrowserUrlPolicy.IsPublicAddress))return addresses;
        static bool FakeIp(IPAddress ip){var bytes=ip.GetAddressBytes();return bytes.Length==4&&bytes[0]==198&&bytes[1] is 18 or 19;}
        if(addresses.Length==0||!addresses.All(FakeIp))throw new HttpRequestException("已阻止本机、内网或非公开地址。");
        using var reply=await publicDns.GetAsync("https://1.1.1.1/dns-query?name="+Uri.EscapeDataString(host)+"&type=A",HttpCompletionOption.ResponseHeadersRead,ct);
        reply.EnsureSuccessStatusCode();await using var stream=await reply.Content.ReadAsStreamAsync(ct);
        var bytes=new byte[65537];int length=0,read;
        while(length<bytes.Length&&(read=await stream.ReadAsync(bytes.AsMemory(length),ct))>0)length+=read;
        if(length>65536)throw new HttpRequestException("公开DNS响应超过大小限制。");
        using var json=JsonDocument.Parse(bytes.AsMemory(0,length));
        if(json.RootElement.GetProperty("Status").GetInt32()!=0||!json.RootElement.TryGetProperty("Answer",out var answers))throw new HttpRequestException("公开DNS没有返回可用地址。");
        addresses=answers.EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==1).Select(a=>IPAddress.Parse(a.GetProperty("data").GetString()!)).Distinct().ToArray();
        if(addresses.Length==0||addresses.Any(a=>!BrowserUrlPolicy.IsPublicAddress(a)))throw new HttpRequestException("公开DNS返回非公开地址，已阻止连接。");
        usedPublicDnsFallback=true;return addresses;
    }
    internal async Task<ToolResult> OpenAsync(string url,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var uri=BrowserUrlPolicy.ValidateUri(url);await EnsureAsync(ct);allowWrite=false;await ClearElementsAsync();blockedRequests.Clear();
            await page!.GotoAsync(uri.AbsoluteUri,new(){WaitUntil=WaitUntilState.Commit,Timeout=30000}).WaitAsync(ct);
            return await ObserveAsync(ct);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){return Failure(ex);}
        finally{allowWrite=false;gate.Release();}
    }
    internal async Task<ToolResult> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{allowWrite=false;if(page is null)return new(false,"尚未打开浏览器页面，请先使用browser_open。");return await ObserveAsync(ct);}
        catch(OperationCanceledException){throw;}catch(Exception ex){return Failure(ex);}finally{gate.Release();}
    }
    internal async Task<ToolResult> ClickAsync(string id,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        bool submitted=false;
        try
        {
            var locator=CurrentElement(id);if(await IsLoginAsync())return LoginResult();
            await VerifyApprovalAsync("browser_click",id,"",ct);ct.ThrowIfCancellationRequested();allowWrite=true;submitted=true;await locator.ClickAsync(new(){Timeout=10000}).WaitAsync(ct);
            allowWrite=false;return (await ObserveAsync(ct)) with {BusinessOutcomeVerified=false};
        }
        catch(OperationCanceledException) when(submitted){throw Uncertain("browser_click",id,ct);}catch(OperationCanceledException){throw;}catch(Exception ex){return Failure(ex) with{NeedsHuman=submitted,BusinessOutcomeVerified=submitted?false:null};}finally{allowWrite=false;gate.Release();}
    }
    internal async Task<ToolResult> FillAsync(string id,string value,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        bool submitted=false;
        try
        {
            if(value.Length>10000)throw new ArgumentException("单次填写内容超过10000字符。");
            var locator=CurrentElement(id);if(await IsLoginAsync())return LoginResult();
            var type=(await locator.GetAttributeAsync("type")??"text").ToLowerInvariant();
            if(type is not ("text" or "search" or "email" or "url" or "tel" or "number"))throw new ArgumentException("仅允许普通文本输入框，不允许密码、文件或隐藏输入。");
            var autocomplete=(await locator.GetAttributeAsync("autocomplete")??"").ToLowerInvariant();
            if(autocomplete.Contains("password")||autocomplete.Contains("one-time-code")||autocomplete.StartsWith("cc-"))throw new ArgumentException("凭据、验证码与支付字段须由用户自行填写。");
            await VerifyApprovalAsync("browser_fill",id,value,ct);ct.ThrowIfCancellationRequested();allowWrite=true;submitted=true;await locator.FillAsync(value,new(){Timeout=10000}).WaitAsync(ct);allowWrite=false;
            return (await ObserveAsync(ct)) with {BusinessOutcomeVerified=false};
        }
        catch(OperationCanceledException) when(submitted){throw Uncertain("browser_fill",id,ct);}catch(OperationCanceledException){throw;}catch(Exception ex){return Failure(ex) with{NeedsHuman=submitted,BusinessOutcomeVerified=submitted?false:null};}finally{allowWrite=false;gate.Release();}
    }
    internal async Task<ToolResult> UploadAsync(string id,string[] sources,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        bool submitted=false;
        try
        {
            if(sources.Length is <1 or >5)throw new ArgumentException("一次附加1至5个文件。");
            var input=CurrentElement(id);if(await IsLoginAsync())return LoginResult();
            if(!await input.EvaluateAsync<bool>("element => element instanceof HTMLInputElement && element.type === 'file'")||!await input.IsVisibleAsync()||!await input.IsEnabledAsync())throw new ArgumentException("只能对最近观察到、当前可见且可用的真实file输入框附加文件。");
            if(sources.Length>1&&await input.GetAttributeAsync("multiple") is null)throw new ArgumentException("此文件输入框不支持多个附件。");
            var validated=sources.Select(source=>FilePolicy.UploadFile(paths,source)).ToArray();
            if(validated.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=validated.Length)throw new ArgumentException("不能重复附加同一文件。");
            long total=0;var payloads=new List<FilePayload>();
            foreach(var file in validated)
            {
                ct.ThrowIfCancellationRequested();
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
                var finalPath=new StringBuilder(32768);var resolved=GetFinalPathNameByHandle(stream.SafeFileHandle,finalPath,(uint)finalPath.Capacity,0);
                if(resolved==0||resolved>=finalPath.Capacity||!string.Equals(finalPath.ToString().Replace(@"\\?\", ""),file,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("附件实际打开路径与已验证路径不一致，已拒绝。");
                if(stream.Length>20L*1024*1024||(total+=stream.Length)>32L*1024*1024)throw new ArgumentException("附件大小超出单个20MB、合计32MB的限制。");
                var bytes=new byte[checked((int)stream.Length)];await stream.ReadExactlyAsync(bytes,ct);
                if(bytes.Length>=2&&bytes[0]=='M'&&bytes[1]=='Z'||bytes.Length>=4&&bytes[0]==0x7f&&bytes[1]=='E'&&bytes[2]=='L'&&bytes[3]=='F'||bytes.Length>=2&&bytes[0]=='#'&&bytes[1]=='!')throw new ArgumentException("附件内容具有可执行程序或脚本标记，拒绝上传。");
                var prefix=Encoding.UTF8.GetString(bytes,0,Math.Min(bytes.Length,8192));
                if(System.Text.RegularExpressions.Regex.IsMatch(prefix,@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new ArgumentException("附件包含私钥内容标记，拒绝上传。");
                payloads.Add(new(){Name=Path.GetFileName(file),Buffer=bytes,MimeType=MimeType(file)});
            }
            await VerifyApprovalAsync("browser_upload",id,JsonSerializer.Serialize(sources),ct);ct.ThrowIfCancellationRequested();allowWrite=true;submitted=true;
            await input.SetInputFilesAsync(payloads,new(){Timeout=15000}).WaitAsync(ct);
            var observed=await input.EvaluateAsync<JsonElement>("element => Array.from(element.files || []).map(file => ({ name: file.name, size: file.size, type: file.type }))").WaitAsync(ct);
            allowWrite=false;
            bool verified=observed.ValueKind==JsonValueKind.Array&&observed.GetArrayLength()==payloads.Count;
            if(verified)for(int i=0;i<payloads.Count;i++)verified &= observed[i].GetProperty("name").GetString()==payloads[i].Name&&observed[i].GetProperty("size").GetInt64()==payloads[i].Buffer.Length;
            await ClearElementsAsync();
            return new(verified,verified?"已回读确认文件输入框中的附件名称和大小；尚未验证上传或发送成功。":"已提交附件，但文件列表回读不一致；请人工检查，不能确认成功。",JsonSerializer.Serialize(new{url=page?.Url,elementId=id,attachments=observed,attachedVerified=verified,actionSubmitted=true,businessOutcomeVerified=false,requiresObservation=true,noAutomaticRetry=true,scope="Only input.files attachment state verified; server upload and message sending not verified"}),NeedsHuman:!verified,BusinessOutcomeVerified:false);
        }
        catch(OperationCanceledException) when(submitted){throw Uncertain("browser_upload",id,ct);}catch(OperationCanceledException){throw;}catch(Exception ex){return Failure(ex) with {NeedsHuman=submitted,BusinessOutcomeVerified=false};}finally{allowWrite=false;gate.Release();}
    }
    internal async Task<string> DescribeApprovalAsync(string tool,string id,JsonElement arguments,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var element=CurrentElement(id);if(await IsLoginAsync())throw new InvalidOperationException("页面含登录/验证码表单，不能准备浏览器写入审批。");
            var target=await DescribeElementAsync(element).WaitAsync(ct);
            var argument=tool switch{"browser_fill"=>arguments.GetProperty("value").GetString()!,"browser_upload"=>JsonSerializer.Serialize(arguments.GetProperty("paths").EnumerateArray().Select(p=>p.GetString()!).ToArray()),_=>""};
            string fileInfo="";
            if(tool=="browser_upload")fileInfo=string.Join("\n",arguments.GetProperty("paths").EnumerateArray().Select(p=>{var file=FilePolicy.UploadFile(paths,p.GetString()!);return file+"（"+new FileInfo(file).Length+" 字节）";}));
            approvals[id]=new(tool,target,Hash(argument),page!.Url,DateTimeOffset.UtcNow);
            return "真实浏览器目标（来自当前页面，非模型标题）：\nURL："+page.Url+"\n页面标题："+await page.TitleAsync()+"\n目标控件："+target+"\n动作："+tool+(tool=="browser_fill"?"\n将填写："+argument:tool=="browser_upload"?"\n将附加文件：\n"+fileInfo:"")+"\n该动作可能产生外部副作用。批准后仍会复查真实URL和目标控件；业务结果尚未验证。";
        }
        finally{gate.Release();}
    }
    private async Task VerifyApprovalAsync(string tool,string id,string argument,CancellationToken ct)
    {
        if(!approvals.Remove(id,out var approval)||approval.Tool!=tool||approval.Arguments!=Hash(argument)||DateTimeOffset.UtcNow-approval.Prepared>TimeSpan.FromSeconds(45))throw new ArgumentException("缺少与真实浏览器目标及参数匹配的有效审批上下文，请重新观察并确认。");
        var actual=await DescribeElementAsync(CurrentElement(id)).WaitAsync(ct);
        if(page!.Url!=approval.Url||actual!=approval.Target)throw new ArgumentException("批准后浏览器URL或目标控件已变化，本次操作未提交。");
    }
    private static Task<string> DescribeElementAsync(IElementHandle element)=>element.EvaluateAsync<string>("element => JSON.stringify({tag:element.tagName,type:element.getAttribute('type'),name:element.getAttribute('name'),id:element.id,label:element.getAttribute('aria-label'),text:(element.innerText||'').slice(0,1500),href:element.getAttribute('href'),formAction:element.form?element.form.action:null,formMethod:element.form?element.form.method:null,disabled:!!element.disabled})");
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private ActionOutcomeUncertainException Uncertain(string tool,string id,CancellationToken ct)
    {allowWrite=false;approvals.Clear();return new("浏览器操作提交后收到取消；附件、填写或点击可能已生效，禁止自动重试。",JsonSerializer.Serialize(new{tool,elementId=id,url=page?.Url,actionSubmitted=true,actionMayStillComplete=true,businessOutcomeVerified=false,noAutomaticRetry=true}),ct);}
    // Test-only fixture bootstrap: no server, private URL exemption, or new production tool.
    internal async Task<ToolResult> LoadFixtureForTestingAsync(string html,CancellationToken ct)
    {
        if(!headless)throw new InvalidOperationException("本地HTML夹具仅可用于内部无界面测试实例。");
        await gate.WaitAsync(ct);
        try{await EnsureAsync(ct);allowWrite=false;await page!.SetContentAsync(html,new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=10000}).WaitAsync(ct);return await ObserveAsync(ct);}
        finally{gate.Release();}
    }
    private static string MimeType(string path)=>Path.GetExtension(path).ToLowerInvariant() switch
    {".txt" or ".md" or ".csv" or ".tsv"=>"text/plain",".pdf"=>"application/pdf",".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".webp"=>"image/webp",".gif"=>"image/gif",".bmp"=>"image/bmp",".mp4"=>"video/mp4",".mp3"=>"audio/mpeg",".wav"=>"audio/wav",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",".pptx"=>"application/vnd.openxmlformats-officedocument.presentationml.presentation",_=>"application/octet-stream"};
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint capacity,uint flags);
    private IElementHandle CurrentElement(string id)
    {
        if(page is null||page.Url!=observedUrl||!elements.TryGetValue(id,out var element))throw new ArgumentException("元素编号已失效，请先browser_read获取最新elementId。");
        return element;
    }
    private async Task EnsureAsync(CancellationToken ct)
    {
        if(context is not null&&page is not null&&!page.IsClosed)return;
        playwright??=await Playwright.CreateAsync().WaitAsync(ct);
        var profile=Path.Combine(paths.Data,"BrowserProfile");Directory.CreateDirectory(profile);
        if(FilePolicy.HasReparsePoint(profile))throw new ArgumentException("浏览器资料目录不能为链接。");
        foreach(var channel in new[]{"chrome","msedge"})
        {
            try
            {
                context=await playwright.Chromium.LaunchPersistentContextAsync(profile,new(){Channel=channel,Headless=headless,AcceptDownloads=false,ServiceWorkers=ServiceWorkerPolicy.Block,Timeout=25000,Args=["--disable-background-networking","--disable-component-update","--disable-sync","--no-first-run","--disable-quic","--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]}).WaitAsync(ct);
                break;
            }
            catch(PlaywrightException) { }
        }
        if(context is null)throw new InvalidOperationException("未找到可启动的Edge或Chrome。请安装其中一个浏览器后重试。");
        await context.RouteAsync("**/*",ForwardRequestAsync);
        await context.RouteWebSocketAsync("**/*",async route=>await route.CloseAsync());
        page=context.Pages.FirstOrDefault()??await context.NewPageAsync();
        context.Page+=(_,popup)=>{_ = popup.CloseAsync();};
        page.Dialog+=(_,dialog)=>{_ = dialog.DismissAsync();};
        page.Download+=(_,download)=>{TraceNetwork("download blocked: "+download.SuggestedFilename);_ = download.CancelAsync();};
        page.SetDefaultTimeout(10000);
    }
    private async Task ForwardRequestAsync(IRoute route)
    {
        try
        {
            var request=route.Request;var uri=BrowserUrlPolicy.ValidateUri(request.Url);
            TraceNetwork("route: "+request.Method+" "+uri.Host);
            if(!allowWrite && request.Method is not ("GET" or "HEAD")){await RejectAsync(route,"未经确认的写请求");return;}
            using var message=new HttpRequestMessage(new HttpMethod(request.Method),uri);
            if(request.PostDataBuffer is {Length:>0} bytes)
            {
                if(bytes.Length>1_000_000){await RejectAsync(route,"请求体过大");return;}
                message.Content=new ByteArrayContent(bytes);
            }
            TraceNetwork("read request headers");
            foreach(var header in await request.AllHeadersAsync())
            {
                if(new[]{"host","content-length","connection","accept-encoding","proxy-authorization"}.Contains(header.Key,StringComparer.OrdinalIgnoreCase))continue;
                if(!message.Headers.TryAddWithoutValidation(header.Key,header.Value))message.Content?.Headers.TryAddWithoutValidation(header.Key,header.Value);
            }
            TraceNetwork("send HTTP request");
            using var response=await transport.SendAsync(message,HttpCompletionOption.ResponseHeadersRead);
            TraceNetwork("HTTP status: "+(int)response.StatusCode);
            if(response.Content.Headers.ContentLength>8*1024*1024){await RejectAsync(route,"资源超过8MB");return;}
            await using var incoming=await response.Content.ReadAsStreamAsync();using var buffer=new MemoryStream();var block=new byte[32768];int read;
            while((read=await incoming.ReadAsync(block))>0){if(buffer.Length+read>8*1024*1024){await RejectAsync(route,"资源超过8MB");return;}buffer.Write(block,0,read);}
            TraceNetwork("body received: "+buffer.Length);
            var headers=response.Headers.Concat(response.Content.Headers).Where(h=>!new[]{"content-encoding","content-length","transfer-encoding","connection"}.Contains(h.Key,StringComparer.OrdinalIgnoreCase)).ToDictionary(h=>h.Key.ToLowerInvariant(),h=>string.Join(h.Key.Equals("set-cookie",StringComparison.OrdinalIgnoreCase)?"\n":", ",h.Value),StringComparer.OrdinalIgnoreCase);
            headers["content-length"]=buffer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TraceNetwork("response content type: "+response.Content.Headers.ContentType);
            await route.FulfillAsync(new(){Status=(int)response.StatusCode,Headers=headers,ContentType=response.Content.Headers.ContentType?.ToString(),BodyBytes=buffer.ToArray()});
            TraceNetwork("route fulfilled");
        }
        catch(Exception ex){try{await RejectAsync(route,"请求失败或地址被保护策略阻止 ("+ex.GetType().Name+": "+ex.Message+")");}catch(PlaywrightException){}}
    }
    private async Task RejectAsync(IRoute route,string reason)
    {lock(blockedRequests){if(blockedRequests.Count<20)blockedRequests.Add(reason+": "+new Uri(route.Request.Url).GetLeftPart(UriPartial.Authority));}await route.AbortAsync("blockedbyclient");}
    private async Task<bool> IsLoginAsync()
    {
        if(page is null)return false;
        var fields=page.Locator("input[type=password], input[autocomplete='one-time-code'], input[autocomplete='current-password'], input[autocomplete='new-password']");
        for(int i=0;i<Math.Min(await fields.CountAsync(),30);i++)if(await fields.Nth(i).IsVisibleAsync())return true;
        var forms=page.Locator("form");
        for(int i=0;i<Math.Min(await forms.CountAsync(),20);i++)
        {
            var form=forms.Nth(i);if(!await form.IsVisibleAsync())continue;
            var username=form.Locator("input[type=email], input[autocomplete=username], input[name=username]");
            if(await username.CountAsync()==0)continue;
            var buttons=form.Locator("button, input[type=submit]");
            for(int j=0;j<Math.Min(await buttons.CountAsync(),15);j++)
            {
                var button=buttons.Nth(j);var label=(await button.InnerTextAsync()+" "+await button.GetAttributeAsync("value")).Trim();
                if(System.Text.RegularExpressions.Regex.IsMatch(label,@"^(sign\s*in|log\s*in|登录|登入|继续登录)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return true;
            }
        }
        return false;
    }
    private ToolResult LoginResult()=>new(false,"当前页面需要登录或验证码。首版受限浏览器尚不支持登录提交，请提供公开页面或导出的文档内容。",JsonSerializer.Serialize(new{url=page?.Url,reason="Visible credential input or login form detected; authenticated login submission not supported",browserProfile=Path.Combine(paths.Data,"BrowserProfile")}),NeedsHuman:true);
    private async Task<ToolResult> ObserveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(page is null||page.IsClosed)return new(false,"浏览器页面已关闭，请重新打开。");
        await ClearElementsAsync();observation++;observedUrl=page.Url;
        var text=await page.Locator("body").InnerTextAsync(new(){Timeout=10000}).WaitAsync(ct);
        var records=new List<object>();var locators=page.Locator("a[href],button,input:not([type=hidden]):not([type=password]),textarea,select,[role=button],[role=link]");
        int count=await locators.CountAsync();
        for(int i=0;i<Math.Min(count,600)&&records.Count<80;i++)
        {
            ct.ThrowIfCancellationRequested();var item=locators.Nth(i);if(!await item.IsVisibleAsync())continue;
            var id=$"e{observation}_{records.Count+1}";
            var aria=await item.GetAttributeAsync("aria-label");var label=aria??await item.InnerTextAsync();
            var type=await item.GetAttributeAsync("type");var placeholder=await item.GetAttributeAsync("placeholder");var href=await item.GetAttributeAsync("href");
            // Element handles pin the observed node so later DOM insertion cannot retarget an nth locator.
            var handle=await item.ElementHandleAsync();if(handle is null)continue;
            elements[id]=handle;
            records.Add(new{elementId=id,label=(label??"")[..Math.Min((label??"").Length,300)],type,placeholder,href,enabled=await item.IsEnabledAsync()});
        }
        var evidence=JsonSerializer.Serialize(new{url=page.Url,title=await page.TitleAsync(),untrustedPageContent=true,text=text[..Math.Min(text.Length,14000)],textTruncated=text.Length>14000,elements=records,elementsTruncated=count>records.Count,blockedRequests=blockedRequests.ToArray(),dns=usedPublicDnsFallback?"Cloudflare HTTPS DNS fallback for local 198.18/15 fake-IP mapping; public results pinned":"System DNS; public results pinned",networkPolicy="Public IP only; no raw model JavaScript; GET/HEAD except explicitly approved click/fill; all HTTP socket destinations checked and pinned"});
        if(await IsLoginAsync())return new(false,"页面需要登录或验证码。首版受限浏览器尚不支持登录提交，请提供公开页面或导出的文档内容。",evidence,NeedsHuman:true);
        return new(true,$"已读取页面“{await page.TitleAsync()}”，观察到{records.Count}个可见元素。",evidence);
    }
    private ToolResult Failure(Exception ex)
    {
        string[] blocked;lock(blockedRequests)blocked=blockedRequests.ToArray();
        return new(false,ex is ArgumentException or InvalidOperationException?ex.Message:"浏览器操作未完成：网站不可达、浏览器未就绪或页面元素已变化。请读取页面后重试。",JsonSerializer.Serialize(new{completed=false,errorCategory=ex.GetType().Name,detail=ex.Message[..Math.Min(ex.Message.Length,1200)],currentUrl=page?.Url,closed=page?.IsClosed,blockedRequests=blocked,networkEvents=networkEvents.ToArray()}));
    }
    private void TraceNetwork(string message){networkEvents.Enqueue(message);while(networkEvents.Count>40)networkEvents.TryDequeue(out _);}
    private async Task ClearElementsAsync()
    {
        foreach(var element in elements.Values){try{await element.DisposeAsync();}catch(PlaywrightException){}}
        elements.Clear();approvals.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        await ClearElementsAsync();if(context is not null)await context.CloseAsync();playwright?.Dispose();transport.Dispose();publicDns.Dispose();gate.Dispose();
    }
}

public static class BrowserUrlPolicy
{
    public static Uri ValidateUri(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||!string.IsNullOrEmpty(uri.UserInfo)||uri.Port is not (80 or 443))throw new ArgumentException("仅允许无内嵌凭据、标准端口的公开HTTP/HTTPS地址。");
        var host=uri.DnsSafeHost.TrimEnd('.');
        if(host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".localhost",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".local",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".internal",StringComparison.OrdinalIgnoreCase)||!host.Contains('.')&&!IPAddress.TryParse(host,out _))throw new ArgumentException("本机、内网或单标签地址不允许浏览。");
        if(IPAddress.TryParse(host,out var ip)&&!IsPublicAddress(ip))throw new ArgumentException("本机、内网或非公开IP地址不允许浏览。");
        return uri;
    }
    public static bool IsPublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        if(IPAddress.IsLoopback(address))return false;
        var b=address.GetAddressBytes();
        if(address.AddressFamily==AddressFamily.InterNetwork)
        {
            if(b[0] is 0 or 10 or 127||b[0]>=224)return false;
            if(b[0]==100&&b[1]>=64&&b[1]<=127||b[0]==169&&b[1]==254||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168||b[0]==192&&b[1]==0||b[0]==192&&b[1]==88&&b[2]==99||b[0]==198&&b[1] is 18 or 19||b[0]==198&&b[1]==51&&b[2]==100||b[0]==203&&b[1]==0&&b[2]==113)return false;
            return true;
        }
        // IPv6 only global-unicast 2000::/3; reject documentation and transition mechanisms.
        if(address.AddressFamily!=AddressFamily.InterNetworkV6||(b[0]&0xe0)!=0x20)return false;
        if(b[0]==0x20&&b[1]==0x01&&(b[2]==0x0d&&b[3]==0xb8||b[2]==0x00)||b[0]==0x20&&b[1]==0x02)return false;
        return true;
    }
}
