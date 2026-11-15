using System.Text.Json;
using System.IO;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class ExecutionBoundaryRegression
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); passed++; }
        (string ToolName, JsonElement Arguments) Resolve(string json) => CodexAppServerClient.ResolveUniversalToolCall(JsonDocument.Parse(json).RootElement);
        void Reject(string json, string field)
        {
            try { Resolve(json); throw new Exception("invalid payload was accepted: " + json); }
            catch (ArgumentException ex) { Check(ex.Message.Contains(field), "missing repair guidance: " + field); }
        }
        var typed = Resolve("""{"c":"type_text","a":{"target":"search","text":"运营"}}""");
        Check(typed.Arguments.GetProperty("elementName").GetString() == "search", "recorded target alias lost");
        var clicked = Resolve("""{"c":"click_control","a":{"window":"fixture","control":"允许"}}""");
        Check(clicked.Arguments.GetProperty("windowTitle").GetString() == "fixture" &&
            clicked.Arguments.GetProperty("elementName").GetString() == "允许", "recorded control/window alias lost");
        Reject("""{"c":"type_text","a":{"text":"运营"}}""", "elementName");
        Reject("""{"c":"click_control","a":{}}""", "elementName");
        Reject("""{"c":"click_point","a":{"x":10,"y":20}}""", "elementDescription");
        Reject("""{"c":"click_point","a":{"x":-1,"y":20,"elementDescription":"fixture"}}""", "x");
        Reject("""{"c":"press_keys","a":{"keys":"ctrl+a"}}""", "purpose");
        Reject("""{"c":"type_active","a":{"text":"hello"}}""", "fieldDescription");
        Reject("""{"c":"type_text","a":{"target":"A","elementName":"B","text":"C"}}""", "冲突");
        Reject("""{"c":"inspect_windows","a":{"root":"D:\\fixture"}}""", "root");
        Reject("""{"c":"type_text","a":{"elementName":"search","text":42}}""", "text");
        Reject("""{"c":"create_presentation","a":{"title":"t","slides":[{"title":"a"}]}}""", "body");
        Check(Resolve("""{"c":"type_active","a":{"text":"你好","fieldDescription":"搜索框"}}""").ToolName == "huitian_type_active", "active input explicitly available");
        Check(Resolve("""{"c":"capture_screen","a":{}}""").ToolName == "huitian_capture_screen", "valid empty observation args rejected");
        Check(Resolve("""{"c":"respond","a":{"message":"测试文字"}}""").Arguments.GetProperty("text").GetString() == "测试文字", "response capability/schema or compatibility alias missing");
        Check(Resolve("""{"c":"find_media","a":{"query":"测试片名","kind":"video"}}""").ToolName == "huitian_find_media", "media capability schema missing");
        Check(Resolve("""{"c":"send_chat_message","a":{"platform":"微信","contact":"虚构联系人","message":"测试"}}""").ToolName == "huitian_send_chat_message", "Unicode enum semantics must not depend on escaping");
        foreach (var id in new[] { "Weixin", "weixin", " WeChat ", "WECHAT", "微信", "qq", " QQ " })
        {
            var canonical = id.Trim().Equals("qq", StringComparison.OrdinalIgnoreCase) ? "QQ" : "微信";
            var send = Resolve(JsonSerializer.Serialize(new { c = "send_chat_message", a = new {
                platform = id, contact = "虚构 Weixin 联系人", message = "  Weixin 不是替换正文的理由。  " } }));
            Check(send.Arguments.GetProperty("platform").GetString() == canonical, "protocol identity not normalized");
            Check(send.Arguments.GetProperty("message").GetString() == "  Weixin 不是替换正文的理由。  " &&
                send.Arguments.GetProperty("contact").GetString() == "虚构 Weixin 联系人", "identity normalization changed content/recipient");
            Check(Resolve(JsonSerializer.Serialize(new { c = "send_chat_files", a = new {
                platform = id, contact = "虚构联系人", filePaths = new[] { @"D:\fixture.txt" } } }))
                .Arguments.GetProperty("platform").GetString() == canonical, "file sender identity differs from message sender");
        }
        foreach (var unsupported in new[] { "WeCom", "企业微信", "WeChat.exe", "wechat or qq" })
            Reject(JsonSerializer.Serialize(new { c = "send_chat_message", a = new {
                platform = unsupported, contact = "虚构联系人", message = "fixture" } }), "允许值：微信、QQ");
        Check(Resolve("""{"c":"modify_file","a":{"path":"D:\\fixture.pptx","presentationStyle":{"backgroundColor":"FFFFFF","fontColor":"000000"}}}""").ToolName == "huitian_modify_file", "formatting contract missing");
        Reject("""{"c":"request_user","a":{"reason":"正在打开浏览器搜索杭州新媒体运营岗位"}}""", "kind");
        Check(CodexAppServerClient.ParseDeliverables(JsonSerializer.SerializeToElement(new { d = new[] {
            new { kind = "slides", artifactKey = "edited", format = "pptx" }, new { kind = "modified", artifactKey = "edited", format = "pptx" } } }))
            .All(d => d.ArtifactKey == "edited"), "universal contract must retain explicit artifact identity");

        Check(UiInterventionDetector.Detect([new("Text", "BOSS直聘APP扫码登录"), new("Button", "点击刷新")])?.Title == "需要你完成登录", "QR wall missed");
        Check(UiInterventionDetector.Detect([new("Text", "请打开 Soul App 扫码登录"), new("Image", ""), new("Text", "使用手机扫一扫")])?.Title == "需要你完成登录", "unnamed social-site QR wall missed");
        Check(UiInterventionDetector.Detect([new("Text", "二维码登录"), new("Text", "请使用手机扫码完成登录")])?.Title == "需要你完成登录",
            "canvas-rendered QR login without accessible image or refresh button missed");
        Check(UiInterventionDetector.Detect([new("Text", "扫码登录"), new("Text", "二维码已失效，请使用抖音 App 重新扫码")])?.Title == "需要你完成登录",
            "expired social-site QR login missed");
        Check(UiInterventionDetector.Detect([new("Text", "验证码登录/注册"), new("Edit", "手机号"), new("Button", "获取验证码")]) is not null, "OTP wall missed");
        Check(UiInterventionDetector.Detect([new("Text", "短信验证码"), new("Edit", "手机号码"), new("Button", "发送短信验证码")])?.Title == "需要你完成登录", "SMS-only social-site wall missed");
        Check(UiInterventionDetector.Detect([new("Text", "短信登录"), new("Edit", "手机号码"), new("Button", "获取验证码")])?.Title == "需要你完成登录", "SMS sign-in before code entry missed");
        Check(UiInterventionDetector.Detect([new("Text", "Sign in"), new("Edit", "Password", true)]) is not null, "password wall missed");
        Check(UiInterventionDetector.Detect([new("Text", "www.example.com 想要"), new("Text", "访问此设备上的其他应用和服务"),new("Button", "允许"),new("Button", "屏蔽")])?.Title == "需要你确认网站权限", "permission popup missed");
        Check(UiInterventionDetector.Detect([new("Button", "登录"),new("Text", "职位列表"),new("Edit", "搜索职位")]) is null, "ordinary login header must not block");
        Check(UiInterventionDetector.Detect([new("Text", "这是一篇介绍密码和登录的文章")]) is null, "article must not become password form");
        Check(UiInterventionDetector.Detect([new("Text", "扫码登录教程"),new("Button", "扫码登录")]) is null, "QR tutorial/header must not trigger takeover");
        Check(UiInterventionDetector.Detect([new("Text", "扫码登录教程：请使用手机"),new("Image", "")]) is null, "QR tutorial with image must not trigger takeover");
        Check(UiInterventionDetector.Detect([new("Text", "扫码登录教程"),new("Text", "请使用手机扫描二维码完成登录")]) is null,
            "QR tutorial text must not trigger takeover without a live login heading");
        Check(UiInterventionDetector.Detect([new("Text", "二维码"),new("Text", "分享当前页面")]) is null, "share QR must not be login");
        FakeUiNode Node(string role, string name, params FakeUiNode[] children) =>
            new(new VisibleUiControl(role, name), children);
        var longFeed = Enumerable.Range(0, 450).Select(index => Node("Text", $"推荐内容 {index}")).ToArray();
        var loginDialog = Node("Pane", "登录弹窗",
            Node("Text", "二维码登录"), Node("Text", "请使用手机扫码完成登录"));
        foreach (var browserTree in new[]
        {
            Node("Window", "测试浏览器", [.. longFeed, loginDialog]),
            Node("Window", "测试浏览器", [loginDialog, .. longFeed]),
        })
            Check(UiInterventionDetector.DetectPrioritized(browserTree,
                node => node.Control, node => node.Children, () => false, maxControls: 64)?.Title == "需要你完成登录",
                "login dialog must be scanned before a long recommendation feed regardless of sibling order");
        var unnamedDialog = Node("Pane", "",
            Node("Text", "扫码登录"), Node("Text", "二维码已过期，请使用手机重新扫码"));
        Check(UiInterventionDetector.DetectPrioritized(
                Node("Window", "测试浏览器", [.. longFeed, unnamedDialog]),
                node => node.Control, node => node.Children, () => false, maxControls: 600)?.Title == "需要你完成登录",
            "bounded wider scan must reach an unnamed canvas dialog after the former 350-node cutoff");
        Check(UiInterventionDetector.DetectPrioritized(
                Node("Window", "测试浏览器", [.. longFeed, Node("Pane", "分享", Node("Text", "二维码"))]),
                node => node.Control, node => node.Children, () => false, maxControls: 600) is null,
            "wider scan must not confuse a share QR with a login wall");
        Check(WindowsUserInterventionObserver.ShouldInspectObservedPage("huitian_capture_screen") &&
              WindowsUserInterventionObserver.ShouldInspectObservedPage("huitian_inspect_windows") &&
              !WindowsUserInterventionObserver.ShouldInspectObservedPage("huitian_search_web"),
            "new login dialog must be checked after real page observation, not public web search");
        var actionApprovals = new WindowsToolBridge();
        Check(!actionApprovals.RequiresApproval("huitian_click_control", JsonSerializer.SerializeToElement(new { elementName = "登录" })) &&
              !actionApprovals.RequiresApproval("huitian_click_point", JsonSerializer.SerializeToElement(new { elementDescription = "注册入口" })) &&
              !actionApprovals.RequiresApproval("huitian_press_keys", JsonSerializer.SerializeToElement(new { purpose = "打开登录页面" })),
            "opening a login or registration page must not summon the user");
        Check(actionApprovals.RequiresApproval("huitian_type_text", JsonSerializer.SerializeToElement(new { elementName = "短信验证码" })) &&
              actionApprovals.RequiresApproval("huitian_click_control", JsonSerializer.SerializeToElement(new { elementName = "确认付款" })),
            "OTP and payment actions must remain protected");
        var genericApproval = typeof(CodexAppServerClient).GetMethod("RequiresUserApproval",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        bool RequiresGenericApproval(string detail) => (bool)genericApproval.Invoke(null,
            [JsonSerializer.SerializeToElement(new { reason = detail }), detail])!;
        Check(!RequiresGenericApproval("打开登录页面，查看可公开浏览的职位和订单"),
            "generic browser opening must not be blocked by login/order words in the goal");
        Check(RequiresGenericApproval("执行付款") && RequiresGenericApproval("永久删除重要数据"),
            "generic payment and destructive operation must remain protected");
        using var blockedReader = new ManualResetEventSlim();
        var reads = 0;
        var boundedObserver = new WindowsUserInterventionObserver(_ => { Interlocked.Increment(ref reads); blockedReader.Wait(); return null; });
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { await boundedObserver.CheckAsync(null,CancellationToken.None); throw new Exception("expected recoverable observation failure"); }
            catch (UiObservationUnavailableException) { Check(started.ElapsedMilliseconds < 2400, "a stuck UI provider must not block for minutes"); }
            try { await boundedObserver.CheckAsync(null,CancellationToken.None); throw new Exception("expected single-flight failure"); }
            catch (UiObservationUnavailableException) { Check(reads == 1, "a stuck UI provider must not create more workers"); }
        }
        finally { blockedReader.Set(); }
        Check(!WindowsUserInterventionObserver.ShouldInspect("huitian_capture_screen") &&
              !WindowsUserInterventionObserver.ShouldInspect("huitian_inspect_windows"), "read-only recovery must not be blocked by the same failed preflight");
        using var stalledRead = new ManualResetEventSlim();
        var readCalls = 0;
        var reader = new BoundedUiRead();
        DynamicToolResult SlowRead() { Interlocked.Increment(ref readCalls); stalledRead.Wait(); return new(true,"fixture"); }
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Check((await reader.ReadAsync(SlowRead,CancellationToken.None)).ResultCode == "observation_unavailable" && started.ElapsedMilliseconds < 2600,
                "tree reads must stop waiting promptly without requesting user takeover");
            Check((await reader.ReadAsync(SlowRead,CancellationToken.None)).ResultCode == "observation_unavailable" && readCalls == 1,
                "tree reads must remain single flight during provider hangs");
        }
        finally { stalledRead.Set(); }

        var definitions = (object[])typeof(CodexAppServerClient).GetMethod("CreateAllDynamicToolDefinitions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        var desc = (string)definitions[0].GetType().GetProperty("description")!.GetValue(definitions[0])!;
        var registered = definitions.Select(d => JsonSerializer.SerializeToElement(d)).ToArray();
        var names = registered.Select(d => d.GetProperty("name").GetString()).ToHashSet();
        Check(registered[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("c").GetProperty("enum")
            .EnumerateArray().All(c => names.Contains("huitian_" + c.GetString())), "every advertised capability must actually have a schema");
        Check(desc.Contains("type_text{windowTitle?,elementName,text,replace?}") &&
            desc.Contains("click_point{windowTitle?,x,y,elementDescription"), "model must see real UI signatures");

        // Protocol-only fake transport: current process is a liveness sentinel,
        // never a child to terminate. All files go to isolated D: fixtures.
        var paths = new HuiTianPaths(@"D:\HuiTian\Cache\Tests\execution-boundary-" + Guid.NewGuid().ToString("N"));
        paths.EnsureDirectories();
        await using var client = new CodexAppServerClient(paths);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var processField = typeof(CodexAppServerClient).GetField("_process", flags)!;
        var inputField = typeof(CodexAppServerClient).GetField("_input", flags)!;
        using var memory = new MemoryStream();
        await using var writer = new StreamWriter(memory, leaveOpen:true) { AutoFlush=true };
        try
        {
            processField.SetValue(client, System.Diagnostics.Process.GetCurrentProcess());
            inputField.SetValue(client, writer);
            typeof(CodexAppServerClient).GetProperty("CurrentThreadId")!.SetValue(client, "fixture-thread");
            typeof(CodexAppServerClient).GetField("_currentTurnId",flags)!.SetValue(client, "fixture-turn");
            var events = new List<AgentApproval>();
            client.ApprovalRequested += (_, e) => events.Add(e.Approval);
            var handler = typeof(CodexAppServerClient).GetMethod("HandleServerRequestAsync",flags)!;
            using var request = JsonDocument.Parse("""{"params":{"threadId":"fixture-thread","turnId":"fixture-turn","tool":"huitian_execute","arguments":{"c":"type_text","a":{"text":"fixture"}}}}""");
            await (Task)handler.Invoke(client, [100L, "item/tool/call",request.RootElement,CancellationToken.None])!;
            await (Task)handler.Invoke(client, [101L, "item/tool/call",request.RootElement,CancellationToken.None])!;
            Check(events.Count == 0, "invalid model arguments must not summon the user");
            typeof(CodexAppServerClient).GetField("_interventionObserver",flags)!.SetValue(client,
                new WindowsUserInterventionObserver(_ => new UserIntervention("需要你完成登录", "测试扫码墙")));
            using var wall = JsonDocument.Parse("""{"params":{"threadId":"fixture-thread","turnId":"fixture-turn","tool":"huitian_execute","arguments":{"c":"request_user","a":{"kind":"authentication","reason":"请扫码","evidence":"测试扫码墙"}}}}""");
            await (Task)handler.Invoke(client, [110L, "item/tool/call",wall.RootElement,CancellationToken.None])!;
            Check(events.Count == 1, "observed login must still request real user intervention");
            await client.RespondToApprovalAsync(110L,true,false);
            var output = System.Text.Encoding.UTF8.GetString(memory.ToArray());
            Check(output.Contains("重新观察") || output.Contains("\\u91CD\\u65B0"), "manual takeover must demand fresh observation");
            Check(!output.Contains("invalid_capability"), "parameters must not be mislabeled unsupported capability");
            var pauseObserved = typeof(CodexAppServerClient).GetMethod("TryPauseForObservedInterventionAsync",flags)!;
            using var observedParameters = JsonDocument.Parse("""{"threadId":"fixture-thread","turnId":"fixture-turn"}""");
            using var emptyArguments = JsonDocument.Parse("{}");
            var afterScreenRead = (Task<bool>)pauseObserved.Invoke(client,
                [111L,"huitian_capture_screen",emptyArguments.RootElement,
                 new DynamicToolResult(true,"fixture-only screen read"),observedParameters.RootElement,CancellationToken.None])!;
            Check(await afterScreenRead && events.Count == 2 && events[1].Method == "huitian/userIntervention",
                "a real login observed after a completed screen read must alert the user");
            await client.RespondToApprovalAsync(111L,true,false);
            Check(!(await (Task<bool>)pauseObserved.Invoke(client,
                    [112L,"huitian_capture_screen",emptyArguments.RootElement,
                     new DynamicToolResult(false,"read failed"),observedParameters.RootElement,CancellationToken.None])!) &&
                  events.Count == 2,
                "failed observations must not create an unactionable login alert");
            using var observerEntered = new ManualResetEventSlim();
            using var observerRelease = new ManualResetEventSlim();
            var delayedObserver = new WindowsUserInterventionObserver(_ =>
            {
                observerEntered.Set(); observerRelease.Wait();
                return new UserIntervention("需要登录", "fixture only");
            });
            typeof(CodexAppServerClient).GetField("_interventionObserver",flags)!.SetValue(client,delayedObserver);
            using var delayedRequest = JsonDocument.Parse("""{"params":{"threadId":"fixture-thread","turnId":"fixture-turn","tool":"huitian_execute","arguments":{"c":"click_control","a":{"elementName":"fixture-only"}}}}""");
            var pendingObservation=(Task)handler.Invoke(client,[102L,"item/tool/call",delayedRequest.RootElement,CancellationToken.None])!;
            Check(observerEntered.Wait(1000), "observer fixture must start");
            typeof(CodexAppServerClient).GetField("_currentTurnId",flags)!.SetValue(client,null);
            observerRelease.Set();
            await pendingObservation;
            Check(events.Count == 2, "a stopped turn must not publish a late login card");
            typeof(CodexAppServerClient).GetField("_currentTurnId",flags)!.SetValue(client,"fixture-turn");
            var sideGate=(SemaphoreSlim)typeof(CodexAppServerClient).GetField("_sideEffectToolGate",flags)!.GetValue(client)!;
            await sideGate.WaitAsync();
            var scheduler=typeof(CodexAppServerClient).GetMethod("ExecuteScheduledToolAsync",flags)!;
            using var harmless=JsonDocument.Parse("""{"text":"fixture"}""");
            var queued=(Task<DynamicToolResult>)scheduler.Invoke(client,["huitian_respond",harmless.RootElement,CancellationToken.None])!;
            typeof(CodexAppServerClient).GetField("_currentTurnId",flags)!.SetValue(client,"replacement-turn");
            sideGate.Release();
            Check((await queued).ResultCode=="stale_turn", "queued old work must not run after replacement");
        }
        finally { processField.SetValue(client,null); inputField.SetValue(client,null); }
        Console.WriteLine($"PASS {passed} execution boundary checks; no desktop action, contact send or login performed.");
    }

    private sealed record FakeUiNode(VisibleUiControl Control, IReadOnlyList<FakeUiNode> Children);
}
