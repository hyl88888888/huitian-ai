using System.Diagnostics;
using System.Text.Json;
using HuiTian.Business.Core;
using HuiTian.Business.Runtime;

var paths=BusinessPaths.Discover();
paths.EnsureDirectories();
await using var runtime=new LocalRuntimeManager(paths);
Console.WriteLine(JsonSerializer.Serialize(await runtime.GetStatusAsync()));
if(args.Contains("--probe"))
{
    using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var timer=Stopwatch.StartNew();
    await runtime.EnsureReadyAsync(new Progress<string>(Console.WriteLine),cancel.Token);
    var startMs=timer.ElapsedMilliseconds;
    using var client=new LocalModelClient("http://127.0.0.1:18280/v1/chat/completions",LocalRuntimeManager.Alias);
    timer.Restart();
    var result=await client.CompleteAsync([new("system","用中文简短回答，不输出思考过程。"),new("user","用一句话解释电子表格是什么。")],false,160,cancel.Token);
    Console.WriteLine(JsonSerializer.Serialize(new{startupMs=startMs,replyMs=timer.ElapsedMilliseconds,reply=result},new JsonSerializerOptions{WriteIndented=true}));
}
