using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HuiTian.Infrastructure;
using HuiTian.Core;

internal static class LiveToolContractProbe
{
    internal static async Task RunAsync(bool delivery = false, bool startLocalCore = false)
    {
        // Generation only. Never dispatch any returned tool to Windows.
        var paths = new HuiTianPaths();
        await using var ownedCore = startLocalCore ? new LocalBrainServer(paths) : null;
        if (ownedCore is not null)
        {
            if (Process.GetProcessesByName("HuiTian.App").Length > 0 || Process.GetProcessesByName("llama-server").Length > 0)
                throw new InvalidOperationException("Do not duplicate an existing application/model.");
            using var downloader = new SegmentedDownloadEngine();
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            Console.WriteLine("Starting existing local model for generation-only tests; no desktop control.");
            await ownedCore.StartAsync(new LocalBrainInstaller(paths, downloader).GetInstalled(CapabilityTierId.Huang), startup.Token);
        }
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18180"), Timeout = TimeSpan.FromSeconds(85) };
        var models = await http.GetFromJsonAsync<JsonElement>("/v1/models");
        var model = models.GetProperty("data")[0].GetProperty("id").GetString();
        var definitions = (object[])typeof(CodexAppServerClient).GetMethod("CreateAllDynamicToolDefinitions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        var executor = JsonSerializer.SerializeToElement(definitions[0]);
        var function = new {name="huitian_execute",description=executor.GetProperty("description").GetString(),parameters=executor.GetProperty("inputSchema")};
        var cases = delivery ? new[]
        {
            new { id="unseen_browse_without_takeover", request="去 https://example.org 替我找找编辑岗位，地点南京，我现在只能线上见面。", state="已知目标网址，无登录墙、无权限弹窗，尚未打开浏览器。先打开用户指定网址。", expected="open_browser" },
            new { id="unseen_ppt_style", request="这套材料打印太费墨了，全套换成浅灰底（EEEEEE）、深蓝字（112244），内容不要动。", state="当前唯一附件绝对路径 D:\\fixture\\handout.pptx。此任务只改变格式，不需要读取正文。", expected="modify_file" },
            new { id="unseen_answer", request="ROI是什么意思？简单解释给我听就行。", state="纯文字问题，不要求电脑操作或生成文件。", expected="respond" },
            new { id="unseen_login_header", request="把知识库里有关雨林的材料找出来。", state="窗口=资料站；Edit名称=资料关键词；Button名称=检索。页眉另有登录按钮，但资料检索公开可用，没有登录墙。先填写资料关键词。", expected="type_text" },
        } : new[]
        {
            new { id="named_field", request="在这个资料库里检索海洋生态。", state="窗口标题：资料查询。可见控件：Edit 名称=检索词；Button 名称=查询。检索词输入框为空。下一步填写文字。", expected="type_text" },
            new { id="login_wall", request="帮我进入后台查看最近的订单。", state="目标页面：APP扫码登录。二维码已过期，请点击刷新；需要使用手机扫码。未登录，无订单内容。", expected="request_user" },
            new { id="permission_prompt", request="继续查找资料。", state="浏览器弹窗：example.test 想要访问此设备上的其他应用和服务。按钮：允许，屏蔽。", expected="request_user" },
        };
        var rows = new List<object>();
        var allPassed = true;
        foreach (var item in cases)
        {
            var timer=Stopwatch.StartNew();
            string? capability=null, error=null, output=null;
            try
            {
                using var response=await http.PostAsJsonAsync("/v1/chat/completions",new
                {
                    model,stream=false,temperature=0.1,max_tokens=180,
                    chat_template_kwargs=new {enable_thinking=false},
                    messages=new[] { new {role="system",content="你是回天电脑助理，根据用户目标及已观察的状态选择下一步。严格按工具签名填写参数，不猜测控件。登录、验证码、付款、设备权限交给用户本人。只输出一次工具调用。"},
                        new {role="user",content=item.request+"\n已观察状态："+item.state} },
                    tools=new[] {new {type="function",function}},tool_choice="required",parallel_tool_calls=false,
                });
                response.EnsureSuccessStatusCode();
                var data=await response.Content.ReadFromJsonAsync<JsonElement>();
                var arguments=data.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!;
                output=arguments;
                var call=CodexAppServerClient.ResolveUniversalToolCall(JsonDocument.Parse(arguments).RootElement);
                capability=call.ToolName[8..];
            }
            catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
            var ok=error is null && capability==item.expected;
            if (ok && item.id == "unseen_ppt_style")
            {
                using var parsed = JsonDocument.Parse(output!);
                var args = parsed.RootElement.GetProperty("a");
                ok = args.TryGetProperty("presentationStyle", out var style) &&
                    style.TryGetProperty("backgroundColor", out var bg) && bg.GetString()?.TrimStart('#') == "EEEEEE" &&
                    style.TryGetProperty("fontColor", out var color) && color.GetString()?.TrimStart('#') == "112244";
            }
            rows.Add(new{item.id,ok,capability,error,output,elapsedMilliseconds=timer.ElapsedMilliseconds});
            allPassed &= ok;
            Console.WriteLine($"{item.id}: {(ok?"PASS":"FAIL")}, {timer.ElapsedMilliseconds}ms, {capability}, {error}");
        }
        var path=@"D:\HuiTian\Cache\Tests\tool-contract-live-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json";
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(path);
        if (!allPassed) throw new InvalidDataException("Live tool generation checks failed; see the saved report. No generated actions were executed.");
    }
}
