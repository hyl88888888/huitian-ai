using System.Net;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DocumentFormat.OpenXml.Packaging;
using HuiTian.Business.Adapters;
using HuiTian.Business.Core;
using A=DocumentFormat.OpenXml.Drawing;
using P=DocumentFormat.OpenXml.Presentation;
using S=DocumentFormat.OpenXml.Spreadsheet;

namespace HuiTian.Business.Tests;

public static class AdapterRegression
{
    public static async Task<int> RunAsync()
    {
        if(Environment.GetEnvironmentVariable("HUITIAN_DESKTOP_GUARD_PROBE") is {Length:32} probe)
        {
            var guard=DesktopSessionGuard.Get(probe);var entered=guard.TryEnter(out _);if(entered)guard.Exit();Console.WriteLine(entered?"DESKTOP_GUARD_PROBE: UNEXPECTED_ACCESS":"DESKTOP_GUARD_PROBE: BLOCKED");return entered?throw new InvalidOperationException("Cross-process desktop guard was bypassed."):1;
        }
        if(Environment.GetEnvironmentVariable("HUITIAN_BROWSER_SMOKE")=="only")return await RunBrowserSmokeAsync();
        if(Environment.GetEnvironmentVariable("HUITIAN_DESKTOP_SMOKE")=="only")return await RunDesktopSmokeAsync();
        if(Environment.GetEnvironmentVariable("HUITIAN_UPLOAD_SMOKE")=="only")return await RunBrowserUploadSmokeAsync();
        var paths=new BusinessPaths(Path.Combine(@"D:\HuiTianBusiness\Cache\Tests","adapters-"+Guid.NewGuid().ToString("N")));
        paths.EnsureDirectories();await using var tools=new DesktopToolRegistry(paths);int checks=0;
        void Check(bool pass,string description){if(!pass)throw new InvalidOperationException("ADAPTER FAIL: "+description);checks++;Console.WriteLine("ADAPTER PASS: "+description);}
        async Task<ToolResult> Call(string name,object args)=>await tools.ExecuteAsync(name,JsonSerializer.SerializeToElement(args),CancellationToken.None);
        object Creation(string kind,string fileName,string content="测试正文")=>new{kind,fileName,title="测试标题",content,rows=new[]{new[]{"姓名","金额"},new[]{"张三","12"},new[]{"张三","12"},new[]{"李四","25"}},slides=new[]{new{title="第一页",body="客户回访\n跟进结果"},new{title="第二页",body="产品测试"}}};
        Check((await Call("capabilities",new{})).Success,"capabilities succeeds with evidence");
        foreach(var kind in new[]{"txt","md","docx","xlsx","pptx"})
        {
            var result=await Call("create_document",Creation(kind,"created."+kind));
            Check(result.Success,"create "+kind+": "+result.Summary);
            Check(result.Artifacts?.Count==1&&File.Exists(result.Artifacts[0].Path)&&result.Evidence.Contains("reopened"),"physical artifact and reopen evidence "+kind);
            var read=await Call("read_file",new{path=result.Artifacts![0].Path,maxCharacters=64000});
            Check(read.Success&&read.Evidence.Length>20,"read created "+kind);
        }
        var originalText=File.ReadAllText(Path.Combine(paths.Outputs,"created.txt"));
        Check(!(await Call("create_document",Creation("txt","created.txt","覆盖攻击"))).Success&&File.ReadAllText(Path.Combine(paths.Outputs,"created.txt"))==originalText,"refuse overwrite and preserve bytes");
        Check(!(await Call("create_document",Creation("txt","..\\escaped.txt"))).Success,"reject output path traversal");
        Check(!(await Call("create_document",Creation("txt","CON.txt"))).Success,"reject device filename");
        Check(!(await Call("create_document",Creation("txt","LPT9.txt"))).Success,"reject high-numbered device filename");
        Check(!(await Call("create_document",Creation("txt","wrong.exe"))).Success,"reject executable output");
        Check(!(await Call("read_file",new{path=Path.Combine(paths.Outputs,"created.txt"),maxCharacters=100,extra="unknown"})).Success,"reject extra arguments");
        Check(!(await Call("read_file",new{path=Path.Combine(paths.Outputs,"created.txt")})).Success,"reject missing arguments");
        Check(!(await Call("read_file",new{path=Path.Combine(paths.Outputs,"created.txt"),maxCharacters=999999})).Success,"reject excessive read bounds");
        Check(!(await Call("read_file",new{path=Path.Combine(paths.Outputs,"..","Outputs","created.txt"),maxCharacters=10})).Success,"reject source path traversal");
        Check(!(await Call("list_files",new{directory=@"D:\",maxItems=10})).Success,"reject whole-disk scope");
        Check(!(await Call("list_files",new{directory=paths.Data,maxItems=10})).Success,"reject internal data directory");
        Check(!(await Call("list_files",new{directory=@"\\server\share",maxItems=10})).Success,"reject UNC scope");
        var list=await Call("list_files",new{directory=paths.Outputs,maxItems=3});
        Check(list.Success&&list.Evidence.Contains("truncated\":true"),"bounded file listing");
        var search=await Call("search_files",new{directory=paths.Outputs,query="created",maxDepth=2,maxItems=2});
        Check(search.Success&&search.Evidence.Contains("truncated\":true"),"bounded filename search");
        var secret=await Call("create_document",Creation("txt","credentials.txt"));
        Check(secret.Success&&!(await Call("read_file",new{path=secret.Artifacts![0].Path,maxCharacters=100})).Success,"credential filename protected");
        File.WriteAllText(Path.Combine(paths.Outputs,"sample.cs"),"not executed");
        Check(!(await Call("read_file",new{path=Path.Combine(paths.Outputs,"sample.cs"),maxCharacters=100})).Success,"reject application source files");
        var bomb=Path.Combine(paths.Outputs,"oversized.docx");
        using(var zip=ZipFile.Open(bomb,ZipArchiveMode.Create))using(var stream=zip.CreateEntry("word/document.xml",CompressionLevel.SmallestSize).Open()){var chunk=new byte[1024*1024];for(int i=0;i<17;i++)stream.Write(chunk);}
        Check(!(await Call("read_file",new{path=bomb,maxCharacters=100})).Success,"reject compressed oversized Office parts");
        var ppt=Path.Combine(paths.Outputs,"created.pptx");var oldHash=SHA256.HashData(File.ReadAllBytes(ppt));
        var styled=await Call("change_presentation_style",new{path=ppt,fileName="styled.pptx",backgroundColor="102030",fontFamily="Microsoft YaHei",fontColor="FFFFFF"});
        Check(styled.Success,"presentation style copied and verified: "+styled.Summary);
        Check(oldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(ppt))),"presentation source unchanged");
        using(var doc=PresentationDocument.Open(styled.Artifacts![0].Path,false))
        {
            Check(doc.PresentationPart!.SlideParts.All(s=>s.Slide!.CommonSlideData!.Background!.Descendants<A.RgbColorModelHex>().Single().Val=="102030"),"every slide actual background color");
            Check(doc.PresentationPart.SlideParts.All(s=>s.Slide!.Descendants<A.LatinFont>().All(f=>f.Typeface=="Microsoft YaHei")),"actual font properties");
        }
        var workbook=Path.Combine(paths.Outputs,"created.xlsx");oldHash=SHA256.HashData(File.ReadAllBytes(workbook));
        var dedup=await Call("deduplicate_worksheet",new{path=workbook,fileName="dedup.xlsx",sheetName="Sheet1",keyColumns=new[]{"A","B"}});
        Check(dedup.Success,"deduplicate simple worksheet: "+dedup.Summary);
        Check(oldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(workbook))),"worksheet source unchanged");
        using(var doc=SpreadsheetDocument.Open(dedup.Artifacts![0].Path,false))Check(doc.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.Row>().Count()==3,"dedupe removes exactly one duplicate and keeps header");
        var formula=Path.Combine(paths.Outputs,"formula.xlsx");File.Copy(workbook,formula);
        using(var doc=SpreadsheetDocument.Open(formula,true)){var sheet=doc.WorkbookPart!.WorksheetParts.Single().Worksheet!;var cell=sheet.Descendants<S.Cell>().Last();cell.DataType=null;cell.CellFormula=new S.CellFormula("1+1");cell.CellValue=new S.CellValue("2");sheet.Save();}
        Check(!(await Call("deduplicate_worksheet",new{path=formula,fileName="unsafe-dedup.xlsx",sheetName="Sheet1",keyColumns=new[]{"A"}})).Success&&!File.Exists(Path.Combine(paths.Outputs,"unsafe-dedup.xlsx")),"formula workbook refused without output residue");
        Check(!(await Call("open_app",new{appId="powershell -Command dangerous"})).Success,"app launch accepts only exact discovered id");
        Check(tools.Definitions.Where(t=>t.Name is "browser_click" or "browser_fill").All(t=>t.Risk==RiskLevel.External),"browser write tools require external approval");
        Check(tools.Definitions.Single(t=>t.Name=="browser_upload").Risk==RiskLevel.External,"browser attachment tool requires external approval");
        Check(tools.Definitions.Single(t=>t.Name=="desktop_invoke").Risk==RiskLevel.Critical&&tools.Definitions.Single(t=>t.Name=="desktop_fill").Risk==RiskLevel.External,"unknown desktop Invoke requires critical review and fill requires external approval");
        Check(!(await Call("desktop_activate_window",new{windowId="0x12345678"})).Success,"desktop rejects unobserved raw window handle");
        Check(!(await Call("desktop_fill",new{elementId="imaginary-control",value="not written"})).Success,"desktop rejects invented control id");
        Check(!(await Call("browser_read",new{})).Success,"unopened browser fails without desktop interaction");
        foreach(var url in new[]{"file:///C:/test.txt","http://localhost/","http://127.0.0.1/","http://192.168.1.1/","http://169.254.169.254/","http://[::1]/","http://[::ffff:127.0.0.1]/","https://example.com:8443/","http://user:secret@example.com/","http://singlelabel/"})
        {
            bool denied=false;try{BrowserUrlPolicy.ValidateUri(url);}catch(ArgumentException){denied=true;}Check(denied,"reject browser URL "+url);
        }
        foreach(var ip in new[]{"10.1.1.1","172.16.0.1","100.64.0.1","192.0.2.1","198.18.0.1","198.51.100.1","203.0.113.1","224.0.0.1","fc00::1","fe80::1","2001:db8::1","2002:c0a8:101::1","::ffff:192.168.0.1"})Check(!BrowserUrlPolicy.IsPublicAddress(IPAddress.Parse(ip)),"deny nonpublic socket destination "+ip);
        Check(BrowserUrlPolicy.ValidateUri("https://example.com/a?q=b").Host=="example.com"&&BrowserUrlPolicy.IsPublicAddress(IPAddress.Parse("8.8.8.8")),"allow ordinary public HTTPS address");
        Console.WriteLine("ADAPTER FIXTURES: "+paths.Root);
        if(Environment.GetEnvironmentVariable("HUITIAN_BROWSER_SMOKE")=="1")checks+=await RunBrowserSmokeAsync();
        if(Environment.GetEnvironmentVariable("HUITIAN_DESKTOP_SMOKE")=="1")checks+=await RunDesktopSmokeAsync();
        if(Environment.GetEnvironmentVariable("HUITIAN_UPLOAD_SMOKE")=="1")checks+=await RunBrowserUploadSmokeAsync();
        return checks;
    }
    public static async Task<int> RunBrowserSmokeAsync()
    {
        var paths=new BusinessPaths(Path.Combine(@"D:\HuiTianBusiness\Cache\Tests","browser-smoke-"+Guid.NewGuid().ToString("N")));
        paths.EnsureDirectories();var watch=Stopwatch.StartNew();ToolResult result;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using(var browser=new BrowserAdapter(paths,headless:true))
        {
            result=await browser.OpenAsync("https://example.com",deadline.Token);
            Console.WriteLine("BROWSER SMOKE OPEN_MS: "+watch.ElapsedMilliseconds);
            Console.WriteLine("BROWSER SMOKE RESULT: "+JsonSerializer.Serialize(result));
            if(!result.Success)Console.WriteLine("BROWSER SMOKE READ AFTER FAILURE: "+JsonSerializer.Serialize(await browser.ReadAsync(deadline.Token)));
        }
        watch.Stop();
        var passed=result.Success&&result.Evidence.Contains("Example Domain",StringComparison.Ordinal)&&result.Evidence.Contains("https://example.com",StringComparison.Ordinal);
        var evidence=new{passed,elapsedMilliseconds=watch.ElapsedMilliseconds,url="https://example.com",headless=true,profile=Path.Combine(paths.Data,"BrowserProfile"),contextClosed=true,result};
        File.WriteAllText(Path.Combine(paths.Root,"browser-smoke-result.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("BROWSER SMOKE TOTAL_MS: "+watch.ElapsedMilliseconds);
        Console.WriteLine("BROWSER SMOKE FIXTURES: "+paths.Root);
        if(!passed)throw new InvalidOperationException("Actual HTTPS browser smoke failed: "+result.Summary);
        Console.WriteLine("ADAPTER PASS: real isolated headless HTTPS navigation and DOM read");return 1;
    }
    public static async Task<int> RunDesktopSmokeAsync()
    {
        int checks=0;var timings=new List<object>();var evidenceDirectory=Path.Combine(@"D:\HuiTianBusiness\Cache\Tests","desktop-smoke-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(evidenceDirectory);
        void Check(bool pass,string description){if(!pass)throw new InvalidOperationException("DESKTOP FAIL: "+description);checks++;Console.WriteLine("DESKTOP PASS: "+description);}
        static string WindowId(ToolResult result,string title)
        {using var json=JsonDocument.Parse(result.Evidence);return json.RootElement.GetProperty("windows").EnumerateArray().Single(w=>w.GetProperty("title").GetString()==title).GetProperty("windowId").GetString()!;}
        static string ElementId(ToolResult result,string automationId)
        {using var json=JsonDocument.Parse(result.Evidence);return json.RootElement.GetProperty("nodes").EnumerateArray().Single(n=>n.GetProperty("automationId").GetString()==automationId).GetProperty("elementId").GetString()!;}
        static string NamedElement(ToolResult result,string name)
        {using var json=JsonDocument.Parse(result.Evidence);return json.RootElement.GetProperty("nodes").EnumerateArray().Single(n=>n.GetProperty("name").GetString()==name&&n.GetProperty("actions").EnumerateArray().Any(a=>a.GetString()=="select")).GetProperty("elementId").GetString()!;}
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));var ct=deadline.Token;var overall=Stopwatch.StartNew();var isolation=Guid.NewGuid().ToString("N");
        await using(var fixture=await DesktopFixture.StartAsync())
        await using(var desktop=new WindowsDesktopAdapter(testIsolationKey:isolation))
        {
            var watch=Stopwatch.StartNew();var listed=await desktop.ListWindowsAsync(100,ct);var id=WindowId(listed,fixture.Title);
            Check(listed.Success&&id.StartsWith("window_"),"real fixture discovered with opaque window identity");
            Check(!(await desktop.ActivateAsync("123456",ct)).Success,"arbitrary handle is refused");
            var activated=await desktop.ActivateAsync(id,ct);Check(activated.Success&&activated.BusinessOutcomeVerified==true,"observed fixture window activation verified");
            var tree=await desktop.ReadWindowAsync(id,100,8,ct);Check(tree.Success,"actual Windows UIA tree read: "+tree.Summary);
            timings.Add(new{operation="discover-activate-read",milliseconds=watch.ElapsedMilliseconds});
            Check(!tree.Evidence.Contains(DesktopFixture.PasswordSentinel,StringComparison.Ordinal),"password value never exposed in snapshot");
            var password=ElementId(tree,"fixture.password");Check(!(await desktop.FillAsync(password,"should not write",ct)).Success,"password control refuses writes");
            var editor=ElementId(tree,"fixture.editor");var oldButton=ElementId(tree,"fixture.button");
            Check(!(await desktop.FillAsync(editor,"missing approval",ct)).Success,"write without trusted approval context rejected");
            var approval=await desktop.DescribeApprovalAsync(editor,"fill","通用桌面控件回读验证",ct);
            Check(approval.Contains(fixture.Title,StringComparison.Ordinal)&&approval.Contains("测试编辑框",StringComparison.Ordinal)&&!approval.Contains(DesktopFixture.PasswordSentinel,StringComparison.Ordinal),"approval describes actual application window control and protected context");
            var fill=await desktop.FillAsync(editor,"通用桌面控件回读验证",ct);Check(fill.Success&&fill.BusinessOutcomeVerified==false,"real Edit filled and business completion withheld");
            Check(await fixture.TextAsync()=="通用桌面控件回读验证","fixture independently confirms actual text value");
            Check(!(await desktop.InvokeAsync(oldButton,"invoke",ct)).Success,"mutation invalidates old snapshot control ids");
            tree=await desktop.ReadWindowAsync(id,100,8,ct);var button=ElementId(tree,"fixture.button");
            _=await desktop.DescribeApprovalAsync(button,"invoke","",ct);await fixture.SetTextAsync("审批后上下文已变更");
            Check(!(await desktop.InvokeAsync(button,"invoke",ct)).Success&&await fixture.CountAsync()==0,"changed business context after approval prevents action");
            tree=await desktop.ReadWindowAsync(id,100,8,ct);button=ElementId(tree,"fixture.button");
            _=await desktop.DescribeApprovalAsync(button,"invoke","",ct);await fixture.FocusEditorAsync();
            var invoke=await desktop.InvokeAsync(button,"invoke",ct);Check(invoke.Success&&invoke.BusinessOutcomeVerified==false,"actual Invoke submitted without false business success");
            Check(invoke.Success,"focus-only change does not invalidate business context fingerprint");
            Check(await fixture.WaitForCountAsync(1),"fixture button handler actually executed once");
            tree=await desktop.ReadWindowAsync(id,100,8,ct);using(var json=JsonDocument.Parse(tree.Evidence))Check(json.RootElement.GetProperty("nodes").EnumerateArray().Any(n=>n.GetProperty("automationId").GetString()=="fixture.status"&&n.GetProperty("name").GetString()=="测试计数：1"),"post-action UIA observation verifies actual counter result");
            var option=NamedElement(tree,"测试选项B");_=await desktop.DescribeApprovalAsync(option,"select","",ct);var select=await desktop.InvokeAsync(option,"select",ct);
            Check(select.Success&&select.BusinessOutcomeVerified==false&&await fixture.SelectionAsync()=="测试选项B","SelectionItem pattern changes and verifies actual selection");
            var bounded=await desktop.ReadWindowAsync(id,3,1,ct);using(var json=JsonDocument.Parse(bounded.Evidence))Check(bounded.Success&&json.RootElement.GetProperty("nodes").GetArrayLength()<=3&&json.RootElement.GetProperty("truncated").GetBoolean(),"tree node and depth bounds enforced");
            tree=await desktop.ReadWindowAsync(id,100,8,ct);var disabled=ElementId(tree,"fixture.disabled");Check(!(await desktop.InvokeAsync(disabled,"invoke",ct)).Success,"disabled control action refused");
            File.WriteAllText(Path.Combine(evidenceDirectory,"fixture-observation.json"),tree.Evidence);
            await using(var expiring=new WindowsDesktopAdapter(elementLifetime:TimeSpan.FromMilliseconds(120),testIsolationKey:isolation))
            {
                var otherId=WindowId(await expiring.ListWindowsAsync(100,ct),fixture.Title);var snapshot=await expiring.ReadWindowAsync(otherId,100,8,ct);var expired=ElementId(snapshot,"fixture.editor");await Task.Delay(180,ct);
                Check(!(await expiring.FillAsync(expired,"expired",ct)).Success,"expired control ids refused");
            }
            await using(var boundedWorker=new WindowsDesktopAdapter(timeout:TimeSpan.FromMilliseconds(800),testIsolationKey:isolation))
            {
                var otherId=WindowId(await boundedWorker.ListWindowsAsync(100,ct),fixture.Title);var snapshot=await boundedWorker.ReadWindowAsync(otherId,100,8,ct);var blockedButton=ElementId(snapshot,"fixture.button");
                _=await boundedWorker.DescribeApprovalAsync(blockedButton,"invoke","",ct);
                using var release=new ManualResetEventSlim(false);await fixture.FreezeAsync(release);watch.Restart();
                ToolResult timedOut;
                try
                {
                    var pending=boundedWorker.InvokeAsync(blockedButton,"invoke",ct);await Task.Delay(100,ct);
                    var competing=await desktop.ActivateAsync(id,ct);Check(!competing.Success&&competing.Evidence.Contains("writeNotSubmitted",StringComparison.Ordinal),"worker-held session mutex rejects another instance while provider is blocked");
                    timedOut=await pending;
                }
                finally{release.Set();}
                var elapsed=watch.ElapsedMilliseconds;Check(!timedOut.Success&&timedOut.NeedsHuman&&elapsed<2000,"hung provider isolated within two seconds");
                var refused=await boundedWorker.InvokeAsync(blockedButton,"invoke",ct);Check(!refused.Success&&refused.NeedsHuman,"quarantined worker refuses repeated action");
                await Task.Delay(250,ct);Check(await fixture.CountAsync()==1,"timed-out preflight never invokes later or duplicates action");
                timings.Add(new{operation="hung-provider",milliseconds=elapsed,result=timedOut});
            }
            tree=await desktop.ReadWindowAsync(id,100,8,ct);Check(tree.Success,"independent healthy adapter remains usable after quarantined worker");
            await using(var replacement=new WindowsDesktopAdapter(testIsolationKey:isolation))
            {var replacementId=WindowId(await replacement.ListWindowsAsync(100,ct),fixture.Title);Check(!(await replacement.ActivateAsync(replacementId,ct)).Success,"replacement registry cannot bypass quarantined Windows session");}
            var childInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))childInfo.ArgumentList.Add(typeof(AdapterRegression).Assembly.Location);
            childInfo.Environment["HUITIAN_DESKTOP_GUARD_PROBE"]=isolation;childInfo.Environment["HUITIAN_DESKTOP_SMOKE"]="";childInfo.Environment["HUITIAN_UPLOAD_SMOKE"]="";childInfo.Environment["HUITIAN_BROWSER_SMOKE"]="";
            using(var child=Process.Start(childInfo)!)
            {var output=child.StandardOutput.ReadToEndAsync(ct);var errors=child.StandardError.ReadToEndAsync(ct);await child.WaitForExitAsync(ct);var stdout=await output;_=await errors;Check(child.ExitCode==0&&stdout.Contains("DESKTOP_GUARD_PROBE: BLOCKED",StringComparison.Ordinal),"separate process cannot bypass shared desktop quarantine");}
            var cancelScope=Guid.NewGuid().ToString("N");await using(var cancellable=new WindowsDesktopAdapter(testIsolationKey:cancelScope))
            {
                var cancellationId=WindowId(await cancellable.ListWindowsAsync(100,ct),fixture.Title);var snapshot=await cancellable.ReadWindowAsync(cancellationId,100,8,ct);var edit=ElementId(snapshot,"fixture.editor");
                _=await cancellable.DescribeApprovalAsync(edit,"fill","cancelled-after-submission",ct);
                using var entered=new ManualResetEventSlim(false);using var release=new ManualResetEventSlim(false);await fixture.BlockNextTextChangeAsync(entered,release);
                using var cancel=new CancellationTokenSource();var pending=cancellable.FillAsync(edit,"cancelled-after-submission",cancel.Token);ActionOutcomeUncertainException? uncertain=null;
                try
                {
                    Check(await Task.Run(()=>entered.Wait(TimeSpan.FromSeconds(3)),ct),"actual ValuePattern submitted before cancellation");cancel.Cancel();
                    try{_=await pending;}catch(ActionOutcomeUncertainException ex){uncertain=ex;}
                }
                finally{release.Set();}
                Check(uncertain is not null&&uncertain.Evidence.Contains("actionSubmitted",StringComparison.Ordinal),"post-submission cancellation preserves explicit uncertain outcome evidence");
                Check(await fixture.TextAsync()=="cancelled-after-submission","cancellation does not falsely claim the applied value was undone");
                await using var afterCancel=new WindowsDesktopAdapter(testIsolationKey:cancelScope);var freshId=WindowId(await afterCancel.ListWindowsAsync(100,ct),fixture.Title);Check(!(await afterCancel.ActivateAsync(freshId,ct)).Success,"cancelled submitted action quarantines replacement instances");
                timings.Add(new{operation="cancel-after-submission",evidence=uncertain!.Evidence});
            }
        }
        overall.Stop();File.WriteAllText(Path.Combine(evidenceDirectory,"desktop-smoke-result.json"),JsonSerializer.Serialize(new{passed=true,checks,elapsedMilliseconds=overall.ElapsedMilliseconds,timings,fixtureClosed=true,scope="Only a newly-created WPF fixture was activated, inspected, filled, selected, invoked and closed; no customer applications operated"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"DESKTOP SMOKE: {checks} checks; {overall.ElapsedMilliseconds} ms; {evidenceDirectory}");return checks;
    }
    public static async Task<int> RunBrowserUploadSmokeAsync()
    {
        int checks=0;var paths=new BusinessPaths(Path.Combine(@"D:\HuiTianBusiness\Cache\Tests","upload-smoke-"+Guid.NewGuid().ToString("N")));paths.EnsureDirectories();
        void Check(bool pass,string description){if(!pass)throw new InvalidOperationException("UPLOAD FAIL: "+description);checks++;Console.WriteLine("UPLOAD PASS: "+description);}
        static string InputId(ToolResult snapshot,string label)
        {using var json=JsonDocument.Parse(snapshot.Evidence);return json.RootElement.GetProperty("elements").EnumerateArray().Single(e=>e.GetProperty("label").GetString()==label).GetProperty("elementId").GetString()!;}
        var first=Path.Combine(paths.Outputs,"fixture-a.txt");var second=Path.Combine(paths.Outputs,"fixture-b.csv");File.WriteAllText(first,"LOCAL FIXTURE ONLY");File.WriteAllText(second,"item,value\nfixture,2");
        var secret=Path.Combine(paths.Outputs,"credentials.txt");File.WriteAllText(secret,"FAKE CREDENTIAL FIXTURE");var executable=Path.Combine(paths.Outputs,"fixture.exe");File.WriteAllBytes(executable,[77,90,0,0]);
        var disguised=Path.Combine(paths.Outputs,"disguised.pdf");File.WriteAllBytes(disguised,[77,90,0,0]);var huge=Path.Combine(paths.Outputs,"oversize.txt");using(var file=File.Create(huge))file.SetLength(20L*1024*1024+1);
        var keyMaterial=Path.Combine(paths.Outputs,"ordinary-note.txt");File.WriteAllText(keyMaterial,"-----BEGIN PRIVATE KEY-----\nFAKE FIXTURE ONLY\n-----END PRIVATE KEY-----");
        var largeA=Path.Combine(paths.Outputs,"large-a.txt");var largeB=Path.Combine(paths.Outputs,"large-b.txt");using(var file=File.Create(largeA))file.SetLength(17L*1024*1024);using(var file=File.Create(largeB))file.SetLength(17L*1024*1024);
        const string html="""
            <!doctype html><html><head><title>Local attachment fixture</title></head><body>
            <label>Single <input type="file" aria-label="single attachment"></label>
            <label>Multiple <input type="file" multiple aria-label="multiple attachments"></label>
            <input type="text" aria-label="ordinary text"><p id="attachment-state">empty</p>
            <script>document.querySelectorAll('input[type=file]').forEach(input => input.addEventListener('change', () => { document.getElementById('attachment-state').textContent = Array.from(input.files).map(file => file.name + ':' + file.size).join(','); }));</script>
            </body></html>
            """;
        var watch=Stopwatch.StartNew();ToolResult upload;
        await using(var browser=new BrowserAdapter(paths,headless:true))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));var ct=deadline.Token;
            var snapshot=await browser.LoadFixtureForTestingAsync(html,ct);Check(snapshot.Success,"local in-memory HTML fixture observed without web navigation");
            var multi=InputId(snapshot,"multiple attachments");var single=InputId(snapshot,"single attachment");var text=InputId(snapshot,"ordinary text");
            Check(!(await browser.UploadAsync("fabricated",[first],ct)).Success,"unobserved file input id rejected");
            Check(!(await browser.UploadAsync(text,[first],ct)).Success,"ordinary text control cannot receive file payload");
            Check(!(await browser.UploadAsync(single,[first,second],ct)).Success,"single-file input rejects multiple attachments");
            Check(!(await browser.UploadAsync(multi,[secret],ct)).Success,"credential filename cannot be attached");
            Check(!(await browser.UploadAsync(multi,[executable],ct)).Success,"executable file cannot be attached");
            Check(!(await browser.UploadAsync(multi,[disguised],ct)).Success,"executable signature cannot hide under document extension");
            Check(!(await browser.UploadAsync(multi,[keyMaterial],ct)).Success,"private-key content marker cannot hide under ordinary filename");
            Check(!(await browser.UploadAsync(multi,[paths.Outputs],ct)).Success,"directory cannot be attached");
            Check(!(await browser.UploadAsync(multi,[Path.Combine(paths.Outputs,"..","Outputs","fixture-a.txt")],ct)).Success,"traversal path cannot be attached");
            Check(!(await browser.UploadAsync(multi,[huge],ct)).Success,"single attachment byte limit enforced before read");
            Check(!(await browser.UploadAsync(multi,[largeA,largeB],ct)).Success,"combined attachment byte limit enforced before attachment");
            Check(!(await browser.UploadAsync(multi,[first,first],ct)).Success,"duplicate attachment path rejected");
            Check(!(await browser.UploadAsync(multi,[first,first,first,first,first,first],ct)).Success,"attachment count limit enforced");
            var approved=await browser.DescribeApprovalAsync("browser_upload",multi,JsonSerializer.SerializeToElement(new{elementId=multi,paths=new[]{first,second}}),ct);
            Check(approved.Contains("about:blank",StringComparison.Ordinal)&&approved.Contains("multiple attachments",StringComparison.Ordinal)&&approved.Contains("fixture-a.txt",StringComparison.Ordinal),"approval displays actual page target and attachment metadata");
            upload=await browser.UploadAsync(multi,[first,second],ct);
            Check(upload.Success&&upload.BusinessOutcomeVerified==false,"real file input attachment succeeds without claiming sending success: "+upload.Summary);
            using(var json=JsonDocument.Parse(upload.Evidence))
            {
                var attached=json.RootElement.GetProperty("attachments");Check(attached.GetArrayLength()==2&&attached[0].GetProperty("name").GetString()=="fixture-a.txt"&&attached[0].GetProperty("size").GetInt64()==new FileInfo(first).Length&&attached[1].GetProperty("name").GetString()=="fixture-b.csv"&&attached[1].GetProperty("size").GetInt64()==new FileInfo(second).Length,"input.files names and byte sizes read back exactly");
            }
            Check(!(await browser.UploadAsync(multi,[first],ct)).Success,"successful attachment invalidates previous input id");
            var after=await browser.ReadAsync(ct);using(var json=JsonDocument.Parse(after.Evidence))Check(json.RootElement.GetProperty("url").GetString()=="about:blank"&&json.RootElement.GetProperty("text").GetString()!.Contains("fixture-a.txt",StringComparison.Ordinal),"own fixture change listener independently observes attachment, no server used");
        }
        watch.Stop();File.WriteAllText(Path.Combine(paths.Root,"upload-smoke-result.json"),JsonSerializer.Serialize(new{passed=true,checks,elapsedMilliseconds=watch.ElapsedMilliseconds,contextClosed=true,source="In-memory local HTML fixture; no local server, public navigation, or outgoing file upload",upload},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"UPLOAD SMOKE: {checks} checks; {watch.ElapsedMilliseconds} ms; {paths.Root}");return checks;
    }
    private sealed class DesktopFixture : IAsyncDisposable
    {
        internal const string PasswordSentinel="PRIVATE-FIXTURE-PASSWORD-NEVER-EXPOSE";
        internal string Title { get; }="HuiTian 自建自动化回归 "+Guid.NewGuid().ToString("N")[..8];
        private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Window? window;private TextBox? editor;private ListBox? choices;private int count;
        internal static async Task<DesktopFixture> StartAsync()
        {
            var fixture=new DesktopFixture();var thread=new Thread(fixture.Show){IsBackground=true,Name="HuiTian isolated WPF regression fixture"};thread.SetApartmentState(ApartmentState.STA);thread.Start();await fixture.ready.Task.WaitAsync(TimeSpan.FromSeconds(8));return fixture;
        }
        private void Show()
        {
            try
            {
                var panel=new StackPanel{Margin=new Thickness(18)};
                editor=new TextBox{Text="初始值",Margin=new Thickness(0,0,0,8),Height=30};AutomationProperties.SetName(editor,"测试编辑框");AutomationProperties.SetAutomationId(editor,"fixture.editor");panel.Children.Add(editor);
                var password=new PasswordBox{Password=PasswordSentinel,Height=24,Margin=new Thickness(0,0,0,8)};AutomationProperties.SetName(password,"密码");AutomationProperties.SetAutomationId(password,"fixture.password");panel.Children.Add(password);
                var status=new TextBlock{Text="测试计数：0",Margin=new Thickness(0,8,0,8)};AutomationProperties.SetAutomationId(status,"fixture.status");
                var button=new Button{Content="测试计数按钮",Height=30};AutomationProperties.SetAutomationId(button,"fixture.button");button.Click+=(_,_)=>{count++;status.Text="测试计数："+count;};panel.Children.Add(button);panel.Children.Add(status);
                choices=new ListBox{Height=60};choices.Items.Add("测试选项A");choices.Items.Add("测试选项B");panel.Children.Add(choices);
                var disabled=new Button{Content="已禁用的测试按钮",IsEnabled=false};AutomationProperties.SetAutomationId(disabled,"fixture.disabled");panel.Children.Add(disabled);
                window=new Window{Title=Title,Content=panel,Width=480,Height=370,Left=30,Top=30,ShowInTaskbar=false,ShowActivated=true,WindowStartupLocation=WindowStartupLocation.Manual};
                window.Closed+=(_,_)=>{closed.TrySetResult();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);};window.Show();ready.TrySetResult();Dispatcher.Run();
            }
            catch(Exception ex){ready.TrySetException(ex);closed.TrySetResult();}
        }
        internal Task<string> TextAsync()=>window!.Dispatcher.InvokeAsync(()=>editor!.Text).Task;
        internal Task SetTextAsync(string value)=>window!.Dispatcher.InvokeAsync(()=>editor!.Text=value).Task;
        internal Task FocusEditorAsync()=>window!.Dispatcher.InvokeAsync(()=>editor!.Focus()).Task;
        internal Task BlockNextTextChangeAsync(ManualResetEventSlim entered,ManualResetEventSlim release)=>window!.Dispatcher.InvokeAsync(()=>
        {
            TextChangedEventHandler? handler=null;
            handler=(_,_)=>{editor!.TextChanged-=handler;entered.Set();release.Wait(TimeSpan.FromSeconds(4));};
            editor!.TextChanged+=handler;
        }).Task;
        internal Task<int> CountAsync()=>window!.Dispatcher.InvokeAsync(()=>count).Task;
        internal Task<string?> SelectionAsync()=>window!.Dispatcher.InvokeAsync(()=>choices!.SelectedItem?.ToString()).Task;
        internal async Task<bool> WaitForCountAsync(int expected){for(int i=0;i<20;i++){if(await CountAsync()==expected)return true;await Task.Delay(50);}return false;}
        internal Task FreezeAsync(ManualResetEventSlim release)
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _=window!.Dispatcher.InvokeAsync(()=>{entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(4));});return entered.Task;
        }
        public async ValueTask DisposeAsync()
        {if(window is not null){await window.Dispatcher.InvokeAsync(()=>window.Close());await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));}}
    }
}
