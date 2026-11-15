using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace HuiTian.Infrastructure;

internal static class WindowsUiAutomation
{
    private static readonly object WeixinOcrDiagnosticGate = new();
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmChar = 0x0102;
    private const uint WmMouseWheel = 0x020A;
    private const int MkLeftButton = 0x0001;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkReturn = 0x0D;
    private const ushort VkA = 0x41;
    private const ushort VkF = 0x46;
    private const ushort VkV = 0x56;
    private const uint GaRoot = 2;
    private const uint CwpSkipInvisible = 0x0001;
    private const uint CwpSkipDisabled = 0x0002;
    private const uint CwpSkipTransparent = 0x0004;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int MaxCaptureWidth = 1440;
    private const int MaxCaptureHeight = 900;
    private static readonly object CaptureMapSync = new();
    // A compact, offline reading table for common Chinese surname/given-name
    // characters. It is intentionally conservative: unmapped or polyphonic
    // characters are not guessed, and phonetic equality only produces a
    // suggestion below the automatic contact-selection threshold.
    private static readonly IReadOnlyDictionary<char, string> CommonChineseNameReadings =
        new Dictionary<char, string>
        {
            ['赵'] = "zhao", ['钱'] = "qian", ['孙'] = "sun", ['李'] = "li",
            ['周'] = "zhou", ['吴'] = "wu", ['郑'] = "zheng", ['王'] = "wang",
            ['冯'] = "feng", ['陈'] = "chen", ['褚'] = "chu", ['卫'] = "wei",
            ['蒋'] = "jiang", ['沈'] = "shen", ['韩'] = "han", ['杨'] = "yang",
            ['朱'] = "zhu", ['秦'] = "qin", ['尤'] = "you", ['许'] = "xu",
            ['何'] = "he", ['吕'] = "lv", ['施'] = "shi", ['张'] = "zhang",
            ['孔'] = "kong", ['曹'] = "cao", ['严'] = "yan", ['华'] = "hua",
            ['金'] = "jin", ['魏'] = "wei", ['陶'] = "tao", ['姜'] = "jiang",
            ['谢'] = "xie", ['邹'] = "zou", ['喻'] = "yu", ['柏'] = "bai",
            ['水'] = "shui", ['窦'] = "dou", ['章'] = "zhang", ['云'] = "yun",
            ['苏'] = "su", ['潘'] = "pan", ['葛'] = "ge", ['奚'] = "xi",
            ['范'] = "fan", ['彭'] = "peng", ['郎'] = "lang", ['鲁'] = "lu",
            ['韦'] = "wei", ['昌'] = "chang", ['马'] = "ma", ['苗'] = "miao",
            ['凤'] = "feng", ['花'] = "hua", ['方'] = "fang", ['俞'] = "yu",
            ['任'] = "ren", ['袁'] = "yuan", ['柳'] = "liu", ['鲍'] = "bao",
            ['史'] = "shi", ['唐'] = "tang", ['费'] = "fei", ['廉'] = "lian",
            ['岑'] = "cen", ['薛'] = "xue", ['雷'] = "lei", ['贺'] = "he",
            ['倪'] = "ni", ['汤'] = "tang", ['滕'] = "teng", ['殷'] = "yin",
            ['罗'] = "luo", ['毕'] = "bi", ['郝'] = "hao", ['邬'] = "wu",
            ['安'] = "an", ['常'] = "chang", ['乐'] = "yue", ['于'] = "yu",
            ['时'] = "shi", ['傅'] = "fu", ['皮'] = "pi", ['卞'] = "bian",
            ['齐'] = "qi", ['康'] = "kang", ['伍'] = "wu", ['余'] = "yu",
            ['元'] = "yuan", ['卜'] = "bu", ['顾'] = "gu", ['孟'] = "meng",
            ['平'] = "ping", ['黄'] = "huang", ['和'] = "he", ['穆'] = "mu",
            ['萧'] = "xiao", ['尹'] = "yin", ['姚'] = "yao", ['邵'] = "shao",
            ['湛'] = "zhan", ['汪'] = "wang", ['祁'] = "qi", ['毛'] = "mao",
            ['禹'] = "yu", ['狄'] = "di", ['米'] = "mi", ['贝'] = "bei",
            ['明'] = "ming", ['臧'] = "zang", ['计'] = "ji", ['伏'] = "fu",
            ['成'] = "cheng", ['戴'] = "dai", ['宋'] = "song", ['茅'] = "mao",
            ['庞'] = "pang", ['熊'] = "xiong", ['纪'] = "ji", ['舒'] = "shu",
            ['屈'] = "qu", ['项'] = "xiang", ['祝'] = "zhu", ['董'] = "dong",
            ['梁'] = "liang", ['杜'] = "du", ['阮'] = "ruan", ['蓝'] = "lan",
            ['闵'] = "min", ['席'] = "xi", ['季'] = "ji", ['麻'] = "ma",
            ['强'] = "qiang", ['贾'] = "jia", ['路'] = "lu", ['娄'] = "lou",
            ['危'] = "wei", ['江'] = "jiang", ['童'] = "tong", ['颜'] = "yan",
            ['郭'] = "guo", ['梅'] = "mei", ['林'] = "lin", ['刁'] = "diao",
            ['钟'] = "zhong", ['徐'] = "xu", ['邱'] = "qiu", ['骆'] = "luo",
            ['高'] = "gao", ['夏'] = "xia", ['蔡'] = "cai", ['田'] = "tian",
            ['樊'] = "fan", ['胡'] = "hu", ['凌'] = "ling", ['霍'] = "huo",
            ['虞'] = "yu", ['万'] = "wan", ['支'] = "zhi", ['柯'] = "ke",
            ['昝'] = "zan", ['管'] = "guan", ['卢'] = "lu", ['莫'] = "mo",
            ['经'] = "jing", ['房'] = "fang", ['裘'] = "qiu", ['缪'] = "miao",
            ['干'] = "gan", ['解'] = "xie", ['应'] = "ying", ['宗'] = "zong",
            ['丁'] = "ding", ['宣'] = "xuan", ['邓'] = "deng", ['郁'] = "yu",
            ['单'] = "shan", ['杭'] = "hang", ['洪'] = "hong", ['包'] = "bao",
            ['诸'] = "zhu", ['左'] = "zuo", ['石'] = "shi", ['崔'] = "cui",
            ['吉'] = "ji", ['钮'] = "niu", ['龚'] = "gong", ['程'] = "cheng",
            ['嵇'] = "ji", ['邢'] = "xing", ['滑'] = "hua", ['裴'] = "pei",
            ['陆'] = "lu", ['荣'] = "rong", ['翁'] = "weng", ['荀'] = "xun",
            ['羊'] = "yang", ['惠'] = "hui", ['甄'] = "zhen", ['曲'] = "qu",
            ['家'] = "jia", ['封'] = "feng", ['芮'] = "rui", ['羿'] = "yi",
            ['储'] = "chu", ['靳'] = "jin", ['汲'] = "ji", ['邴'] = "bing",
            ['糜'] = "mi", ['松'] = "song", ['井'] = "jing", ['段'] = "duan",
            ['富'] = "fu", ['巫'] = "wu", ['乌'] = "wu", ['焦'] = "jiao",
            ['巴'] = "ba", ['弓'] = "gong", ['牧'] = "mu", ['隗'] = "wei",
            ['山'] = "shan", ['谷'] = "gu", ['车'] = "che", ['侯'] = "hou",
            ['宓'] = "mi", ['蓬'] = "peng", ['全'] = "quan", ['郗'] = "xi",
            ['班'] = "ban", ['仰'] = "yang", ['秋'] = "qiu", ['仲'] = "zhong",
            ['伊'] = "yi", ['宫'] = "gong", ['宁'] = "ning", ['仇'] = "qiu",
            ['栾'] = "luan", ['暴'] = "bao", ['甘'] = "gan", ['钭'] = "tou",
            ['厉'] = "li", ['戎'] = "rong", ['祖'] = "zu", ['武'] = "wu",
            ['刘'] = "liu", ['景'] = "jing", ['詹'] = "zhan", ['束'] = "shu",
            ['龙'] = "long", ['叶'] = "ye", ['幸'] = "xing", ['司'] = "si",
            ['韶'] = "shao", ['黎'] = "li", ['乔'] = "qiao", ['佟'] = "tong",
            ['良'] = "liang", ['凉'] = "liang", ['亮'] = "liang",
            ['宇'] = "yu", ['雨'] = "yu", ['羽'] = "yu", ['语'] = "yu",
            ['伟'] = "wei", ['玮'] = "wei", ['维'] = "wei",
            ['杰'] = "jie", ['洁'] = "jie", ['婕'] = "jie",
            ['娜'] = "na", ['楠'] = "nan", ['南'] = "nan",
            ['俊'] = "jun", ['军'] = "jun", ['君'] = "jun",
            ['涛'] = "tao", ['桃'] = "tao", ['豪'] = "hao", ['浩'] = "hao",
            ['晨'] = "chen", ['辰'] = "chen", ['宸'] = "chen",
            ['轩'] = "xuan", ['萱'] = "xuan", ['璇'] = "xuan",
            ['欣'] = "xin", ['鑫'] = "xin", ['心'] = "xin",
            ['佳'] = "jia", ['嘉'] = "jia", ['琪'] = "qi", ['琦'] = "qi",
        };
    private static ScreenCaptureMap? _lastCaptureMap;

    private static readonly string[] GenericInputNames =
    [
        "输入框",
        "聊天输入框",
        "消息输入框",
        "消息编辑框",
        "编辑框",
        "文本框",
        "输入区域",
        "输入消息",
        "chat input",
        "message input",
        "text box",
        "textbox",
        "input",
    ];

    public static DynamicToolResult Inspect(string? windowTitle, int maxItems)
    {
        try
        {
            var window = FindWindow(windowTitle);
            if (window is null)
            {
                return new DynamicToolResult(false, "没有找到目标窗口。请先打开或激活它。 ");
            }

            var windowName = SafeName(window);
            var nodes = ReadVisibleControlSlice(window);
            var controls = nodes.Select(DescribeInspectionControl);
            if (windowName.Contains("抖音", StringComparison.OrdinalIgnoreCase) ||
                windowName.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            {
                var observedControls = controls.Take(120).ToArray();
                var pageState = DouyinDiscoveryPageAnalyzer.Analyze(windowName, observedControls);
                return new DynamicToolResult(true, FormatInspectionSummary(
                    windowName, observedControls, maxItems, pageState?.ToCompactObservation()));
            }
            return new DynamicToolResult(true,
                FormatInspectionSummary(windowName, controls, maxItems));
        }
        catch (ElementNotAvailableException)
        {
            return new DynamicToolResult(false, "目标窗口刚刚发生变化，请重新观察后再操作。 ");
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"暂时无法读取目标窗口：{exception.Message}");
        }
    }

    /// <summary>
    /// A compact, read-only page state to accompany an already requested
    /// screenshot. This avoids another model round trip merely to discover
    /// that a Douyin video search is not a list of contact profiles.
    /// </summary>
    internal static DynamicToolResult InspectDouyinDiscoveryState(string? windowTitle)
    {
        try
        {
            var window = FindWindow(windowTitle);
            if (window is null)
                return new DynamicToolResult(false, "没有找到抖音目标窗口。");
            var name = SafeName(window);
            var controls = ReadVisibleControlSlice(window)
                .Take(160)
                .Select(DescribeInspectionControl)
                .ToArray();
            var state = DouyinDiscoveryPageAnalyzer.Analyze(name, controls);
            return state is null
                ? new DynamicToolResult(false, "当前页面不是可识别的抖音搜索或用户资料页。")
                : new DynamicToolResult(true, state.ToCompactObservation());
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or
            InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return new DynamicToolResult(false, "抖音页面刚发生变化，稍后可重新观察。");
        }
    }

    internal sealed record InspectionControl(
        string Role, string Name, string AutomationId, bool Enabled,
        bool IncludeWhenUnnamed = false, bool? IsSelected = null,
        string? Value = null, bool IsPassword = false);

    private static InspectionControl DescribeInspectionControl(AutomationElement element)
    {
        var role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
        var name = SafeName(element);
        var value = role == "Hyperlink" || role == "Edit" &&
            (name.Contains("地址", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("address", StringComparison.OrdinalIgnoreCase))
            ? TryReadInspectionValue(element) : null;
        bool? selected = null;
        if (role is "TabItem" or "Button")
        {
            try
            {
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                    pattern is SelectionItemPattern selection)
                    selected = selection.Current.IsSelected;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }
        var isPassword = false;
        if (role == "Edit")
        {
            try { isPassword = element.Current.IsPassword; }
            catch (ElementNotAvailableException) { }
        }
        return new InspectionControl(role, name, SafeAutomationId(element),
            SafeIsEnabled(element), IsInspectableUnnamedInput(element),
            selected, value, isPassword);
    }

    private static string? TryReadInspectionValue(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                   pattern is ValuePattern value ? value.Current.Value : null;
        }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private const int MaximumInspectionItems = 36;
    private const int MaximumInspectionCharacters = 2_600;
    private const int MaximumInspectionNameCharacters = 120;
    private const int MaximumInspectionIdCharacters = 80;
    private static readonly JsonSerializerOptions InspectionJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string FormatInspectionSummary(
        string windowName, IEnumerable<InspectionControl> controls, int maxItems,
        string? pageState = null)
    {
        const string footer = "控件列表可能已截断；点击或输入时使用完整的 elementName 或 automationId，不要拼入 role。未命名输入可用已确认焦点的 type_active。";
        var builder = new StringBuilder();
        var shortWindowName = windowName.Length <= MaximumInspectionNameCharacters
            ? windowName : windowName[..MaximumInspectionNameCharacters];
        builder.AppendLine($"当前窗口：{shortWindowName}");
        if (pageState is { Length: <= 500 }) builder.AppendLine(pageState);
        var itemLimit = Math.Clamp(maxItems, 1, MaximumInspectionItems);
        var written = 0;
        foreach (var control in controls)
        {
            if (written >= itemLimit) break;
            if (string.IsNullOrWhiteSpace(control.Name) && !control.IncludeWhenUnnamed)
                continue;
            var name = control.Name.Length <= MaximumInspectionNameCharacters
                ? control.Name : string.Empty;
            var automationId = control.AutomationId.Length <= MaximumInspectionIdCharacters
                ? control.AutomationId : string.Empty;
            // Truncated locators may resolve to a different control. Keep only
            // complete names or IDs; full page text is available by screenshot.
            if (string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(automationId) &&
                !control.IncludeWhenUnnamed)
                continue;

            var role = control.Role.Length <= 24 ? control.Role : control.Role[..24];
            var line = JsonSerializer.Serialize(new
            {
                role,
                elementName = name,
                automationId,
                enabled = control.Enabled,
            }, InspectionJsonOptions);
            if (builder.Length + line.Length + Environment.NewLine.Length + footer.Length +
                Environment.NewLine.Length > MaximumInspectionCharacters)
                break;

            builder.AppendLine(line);
            written++;
        }

        if (written == 0)
            builder.AppendLine("窗口没有提供可读取的简短控件名称，可改用屏幕观察。");
        builder.Append(footer);
        return builder.ToString();
    }

    public static DynamicToolResult Activate(string? windowTitle)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到要切换到的窗口。 ");
        }

        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (!ActivateWindowHandle(handle))
        {
            return new DynamicToolResult(false, "目标窗口暂时无法切换到前台。 ");
        }

        return new DynamicToolResult(true, $"已切换到：{SafeName(window)}");
    }

    private static List<AutomationElement> ReadVisibleControlSlice(AutomationElement window)
    {
        var result = new List<AutomationElement>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((window, 0));
        var walker = TreeWalker.ControlViewWalker;
        var clock = Stopwatch.StartNew();
        while (queue.Count > 0 && result.Count < 500 && clock.ElapsedMilliseconds < 850)
        {
            var (node, depth) = queue.Dequeue();
            if (node.Current.IsOffscreen) continue;
            if (depth > 0) result.Add(node);
            if (depth >= 14) continue;
            var child = walker.GetFirstChild(node);
            var siblings = 0;
            while (child is not null && siblings++ < 100 && queue.Count < 500 && clock.ElapsedMilliseconds < 850)
            {
                queue.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
        return result;
    }

    public static DynamicToolResult Click(string? windowTitle, string? elementName, int clickCount = 1)
    {
        if (string.IsNullOrWhiteSpace(elementName))
        {
            return new DynamicToolResult(false, "没有指定要点击的按钮或控件名称。 ");
        }

        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        var element = FindNamedElement(window, elementName);
        if (element is null)
        {
            return new DynamicToolResult(false, $"窗口中没有找到“{elementName}”，请重新观察窗口。 ");
        }

        ActivateWindowHandle(new IntPtr(window.Current.NativeWindowHandle));
        clickCount = clickCount == 2 ? 2 : 1;
        if (clickCount == 1 && element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject))
        {
            ((InvokePattern)invokeObject).Invoke();
            return new DynamicToolResult(true, $"已点击：{SafeName(element)}");
        }

        if (clickCount == 1 && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject))
        {
            ((SelectionItemPattern)selectionObject).Select();
            return new DynamicToolResult(true, $"已选择：{SafeName(element)}");
        }

        if (clickCount == 1 && element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObject))
        {
            ((TogglePattern)toggleObject).Toggle();
            return new DynamicToolResult(true, $"已切换：{SafeName(element)}");
        }

        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || double.IsInfinity(bounds.Left) || double.IsInfinity(bounds.Top))
        {
            return new DynamicToolResult(false, "目标控件没有可点击的位置。 ");
        }

        var x = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
        var y = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
        return PostVirtualClick(
            window,
            x,
            y,
            clickCount,
            $"已{(clickCount == 2 ? "双击" : "单击")}：{SafeName(element)}。请重新观察界面确认结果。");
    }

    public static DynamicToolResult TypeText(
        string? windowTitle,
        string? elementName,
        string? text,
        bool replace)
    {
        if (string.IsNullOrWhiteSpace(elementName) || text is null)
        {
            return new DynamicToolResult(false, "没有提供输入框名称或文字内容。 ");
        }

        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        var element = FindNamedElement(window, elementName);
        if (element is null)
        {
            return new DynamicToolResult(false, $"窗口中没有找到输入框“{elementName}”。 ");
        }
        return InputTextIntoElement(window, element, text, replace);
    }

    private static DynamicToolResult InputTextIntoElement(
        AutomationElement window,
        AutomationElement element,
        string text,
        bool replace)
    {
        var topHandle = new IntPtr(window.Current.NativeWindowHandle);
        if (!ActivateWindowHandle(topHandle))
        {
            return new DynamicToolResult(false, "目标窗口暂时无法切换到前台，为避免输入到错误位置，本次没有输入。 ");
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject))
        {
            var pattern = (ValuePattern)valueObject;
            if (pattern.Current.IsReadOnly)
            {
                return new DynamicToolResult(false, "目标输入框是只读的。 ");
            }

            var value = replace ? text : pattern.Current.Value + text;
            pattern.SetValue(value);
            // ValuePattern can update a field without moving keyboard focus.
            // Chat sending relies on Enter reaching the editor, so align focus
            // with the field after the verified value change.
            TryFocusForInput(window, element);
            Thread.Sleep(70);
            var actual = pattern.Current.Value;
            var verified = replace
                ? actual.Equals(value, StringComparison.Ordinal)
                : actual.EndsWith(text, StringComparison.Ordinal);
            return verified
                ? new DynamicToolResult(true, $"已在“{SafeElementLabel(element)}”中输入并复核文字。 ")
                : new DynamicToolResult(false, $"输入后复核不一致；“{SafeElementLabel(element)}”中没有出现预期文字，请换一种输入方式。 ");
        }

        if (!TryFocusForInput(window, element))
        {
            return new DynamicToolResult(false, "目标控件暂时无法获得键盘焦点。请重新观察并点击输入区域。 ");
        }

        Thread.Sleep(80);
        var focusedElement = GetFocusedElementWithin(window);
        var nativeFocus = GetNativeFocusedHandle(topHandle);
        if (focusedElement is null && nativeFocus == IntPtr.Zero)
        {
            return new DynamicToolResult(false, "目标输入区域没有获得焦点。为避免把文字输到别处，本次没有输入。 ");
        }

        if (replace && SendControlShortcut(VkA) == 0)
        {
            return new DynamicToolResult(false, "目标输入区域无法执行全选，未继续替换原文字。 ");
        }

        var dispatch = DispatchTextToFocusedControl(topHandle, nativeFocus, text);
        if (!dispatch.Dispatched)
        {
            return new DynamicToolResult(false, "目标输入区域没有接受键盘输入。请重新观察输入框后再试。 ");
        }

        Thread.Sleep(80);
        var focusVerification = VerifyFocusedValue(text, replace);
        return focusVerification switch
        {
            true when dispatch.ClipboardRestored => new DynamicToolResult(true, $"已在“{SafeElementLabel(element)}”中输入并复核文字。 "),
            true => new DynamicToolResult(true, $"已在“{SafeElementLabel(element)}”中输入并复核文字；系统剪贴板当时被其他程序占用，未能确认已完整恢复。 "),
            false => new DynamicToolResult(false, "输入后复核不一致，请重新观察输入框后换一种方式。 "),
            null when dispatch.ClipboardRestored => new DynamicToolResult(
                true,
                "输入动作已执行，但这个输入框不提供可读值。必须立即重新观察屏幕；确认文字真实出现前不得声称输入成功。 ",
                RequiresVerification: true),
            null => new DynamicToolResult(
                true,
                "输入动作已执行，但这个输入框不提供可读值，且系统剪贴板当时被其他程序占用。必须立即重新观察屏幕，并确认剪贴板内容。 ",
                RequiresVerification: true),
        };
    }

    /// <summary>
    /// A bounded, deterministic fast path for the common "send a message"
    /// workflow. It deliberately prefers accessibility and keyboard focus over
    /// repeated whole-screen screenshots. The visual agent remains the final
    /// fallback when a GPU-rendered client exposes no searchable contact.
    /// </summary>
    public static DynamicToolResult SendChatMessage(
        string? platform,
        string? contactName,
        string? message)
    {
        var requestedPlatform = platform?.Trim();
        if (string.IsNullOrWhiteSpace(requestedPlatform) ||
            string.IsNullOrWhiteSpace(message))
        {
            return new DynamicToolResult(false, "请同时提供聊天软件和要发送的内容。 ");
        }

        if (!IsSupportedChatSendPlatform(requestedPlatform))
            return UnsupportedChatSendPlatform();

        return SendChatMessageCore(requestedPlatform, contactName, message);
    }

    // The UI automation fixture uses a synthetic window title to exercise the
    // contact and editor algorithm. Production callers use the guarded method.
    internal static DynamicToolResult SendChatMessageToSyntheticWindow(
        string windowTitle, string? contactName, string message) =>
        SendChatMessageCore(windowTitle, contactName, message);

    private static DynamicToolResult SendChatMessageCore(
        string requestedPlatform, string? contactName, string message)
    {
        var requestedContact = contactName?.Trim();

        if (message.Length > 20_000)
        {
            return new DynamicToolResult(false, "单条消息内容过长，请先分段后再发送。 ");
        }

        // An empty contact is an explicit product-layer instruction to use the
        // conversation that is already open. It never enters contact search.
        if (string.IsNullOrWhiteSpace(requestedContact))
        {
            return SendToCurrentChatCore(requestedPlatform, message, filePaths: null);
        }

        try
        {
            var target = FindOrOpenVerifiedChat(requestedPlatform, requestedContact);
            return target.Failure ?? SendTextToOpenChat(target, message);
        }
        catch (ElementNotAvailableException)
        {
            return new DynamicToolResult(false, "聊天窗口在操作时发生变化，请重新读取当前窗口后再换下一种路径。 ");
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"聊天消息没有发送完成：{exception.Message}");
        }
    }

    public static DynamicToolResult SendChatPayload(
        string? platform,
        string? contactName,
        string? message,
        IReadOnlyList<string>? filePaths)
    {
        if (!string.IsNullOrWhiteSpace(platform) &&
            !IsSupportedChatSendPlatform(platform))
            return UnsupportedChatSendPlatform();

        var files = NormalizeExistingFilePaths(filePaths);
        return files.Length > 0
            ? SendChatFiles(platform, contactName, files)
            : SendChatMessage(platform, contactName, message);
    }

    /// <summary>
    /// Opens a named chat for observation without entering or sending any
    /// content. The same unique-candidate and title checks used by the send
    /// path apply here, so a parallel session cannot read another contact's
    /// visible messages as its own.
    /// </summary>
    internal static DynamicToolResult OpenVerifiedChatForObservation(
        string platform,
        string recipient)
    {
        if (string.IsNullOrWhiteSpace(platform) ||
            string.IsNullOrWhiteSpace(recipient) || recipient == "@current")
        {
            return new DynamicToolResult(false, "没有可核对的命名联系人。 ");
        }

        try
        {
            var target = FindOrOpenVerifiedChat(platform.Trim(), recipient.Trim());
            return target.Failure ?? new DynamicToolResult(
                true,
                $"已打开并核对联系人“{target.ContactLabel}”的会话；没有输入或发送任何内容。",
                ResultCode: "completed");
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false,
                $"无法核对目标会话：{exception.Message}",
                ResultCode: "target_verification_required");
        }
    }

    public static DynamicToolResult SendChatFiles(
        string? platform,
        string? contactName,
        IReadOnlyList<string>? filePaths)
    {
        var requestedPlatform = platform?.Trim();
        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return new DynamicToolResult(false, "请提供聊天软件。 ");
        }

        if (!IsSupportedChatSendPlatform(requestedPlatform))
            return UnsupportedChatSendPlatform();

        return SendChatFilesCore(requestedPlatform, contactName, filePaths);
    }

    internal static DynamicToolResult SendChatFilesToSyntheticWindow(
        string windowTitle, string? contactName, IReadOnlyList<string>? filePaths) =>
        SendChatFilesCore(windowTitle, contactName, filePaths);

    private static DynamicToolResult SendChatFilesCore(
        string requestedPlatform, string? contactName, IReadOnlyList<string>? filePaths)
    {
        var requestedContact = contactName?.Trim();

        var files = NormalizeExistingFilePaths(filePaths);
        if (files.Length == 0)
        {
            return new DynamicToolResult(false, "没有找到可发送的本机文件；请重新选择文件。 ");
        }

        // Empty contact means the already-open conversation. This path is
        // intentionally separate from recipient search so it cannot switch to
        // an unintended contact.
        if (string.IsNullOrWhiteSpace(requestedContact))
        {
            return SendToCurrentChatCore(requestedPlatform, message: null, files);
        }

        try
        {
            var target = FindOrOpenVerifiedChat(requestedPlatform, requestedContact);
            return target.Failure ?? SendFilesToOpenChat(target, files);
        }
        catch (ElementNotAvailableException)
        {
            return new DynamicToolResult(false, "聊天窗口在操作时发生变化，请重新读取当前窗口后再换下一种路径。 ");
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"聊天文件没有发送完成：{exception.Message}");
        }
    }

    /// <summary>
    /// Completes the second half of the GPU-shell path.  Callers must first
    /// verify the contact in the screenshot returned with
    /// target_verification_required; this method intentionally performs no
    /// search and therefore cannot select another recipient by itself.
    /// </summary>
    public static DynamicToolResult SendToVerifiedCurrentChat(
        string? platform,
        string? message,
        IReadOnlyList<string>? filePaths)
    {
        var requestedPlatform = platform?.Trim();
        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return new DynamicToolResult(false, "没有提供聊天软件。 ");
        }

        if (!IsSupportedChatSendPlatform(requestedPlatform))
            return UnsupportedChatSendPlatform();

        return SendToCurrentChatCore(requestedPlatform, message, filePaths);
    }

    internal static DynamicToolResult SendToSyntheticCurrentChat(
        string windowTitle, string? message, IReadOnlyList<string>? filePaths) =>
        SendToCurrentChatCore(windowTitle, message, filePaths);

    private static DynamicToolResult SendToCurrentChatCore(
        string requestedPlatform, string? message, IReadOnlyList<string>? filePaths)
    {

        if (!string.IsNullOrWhiteSpace(message) && message.Length > 20_000)
        {
            return new DynamicToolResult(false, "单条消息内容过长，请先分段后再发送。 ");
        }

        try
        {
            var window = FindWindow(requestedPlatform);
            if (window is null)
            {
                return new DynamicToolResult(
                    false,
                    $"没有发现已打开的{requestedPlatform}主窗口。请先打开或登录{requestedPlatform}。",
                    ResultCode: "not_ready");
            }

            var topHandle = new IntPtr(window.Current.NativeWindowHandle);
            if (!ActivateWindowHandle(topHandle))
            {
                return new DynamicToolResult(false, $"{requestedPlatform}窗口暂时无法切换到前台。 ");
            }

            var target = new OpenChatTarget(
                window,
                topHandle,
                requestedPlatform,
                Search: null,
                Snapshot: window.FindAll(TreeScope.Descendants, Condition.TrueCondition),
                ContactLabel: "当前会话",
                Strategy: "已核对的当前会话",
                VerificationDetail: "当前会话已由产品层局部画面核对",
                Failure: null);
            var files = NormalizeExistingFilePaths(filePaths);
            if (files.Length > 0)
            {
                return SendFilesToOpenChat(target, files);
            }

            return string.IsNullOrWhiteSpace(message)
                ? new DynamicToolResult(false, "没有提供要发送的消息或文件。 ")
                : SendTextToOpenChat(target, message);
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"当前聊天内容没有发送完成：{exception.Message}");
        }
    }

    internal static bool IsSupportedChatSendPlatform(string? platform) =>
        HuiTian.Core.CommunicationPlatformIdentity.Canonicalize(platform) is "微信" or "QQ" ||
        string.Equals(platform?.Trim(), "腾讯QQ", StringComparison.OrdinalIgnoreCase);

    private static DynamicToolResult UnsupportedChatSendPlatform() => new(
        false,
        "当前专用聊天发送仅支持微信和 QQ；其他平台还没有联系人核验及发送适配。本次没有输入或发送任何内容。",
        ResultCode: "unsupported_platform");

    internal static bool VerifyVisibleChatRecipient(string platform, string recipient)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(recipient))
        {
            return false;
        }

        try
        {
            var window = FindWindow(platform);
            if (window is null) return false;
            var nodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            return VerifyCurrentChatTarget(window, recipient, nodes).Verified;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static string? ReadVisibleChatTitle(string platform)
    {
        try
        {
            var window = FindWindow(platform);
            if (window is null) return null;
            var bounds = window.Current.BoundingRectangle;
            var headerBottom = bounds.Top + Math.Max(150, bounds.Height * 0.28);
            var contentLeft = bounds.Left + bounds.Width * 0.35;
            var nodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < nodes.Count; index++)
            {
                try
                {
                    var element = nodes[index];
                    var current = element.Current;
                    var rectangle = current.BoundingRectangle;
                    if (current.ControlType != ControlType.Text || current.IsOffscreen ||
                        !IsUsableBounds(rectangle) || rectangle.Top > headerBottom ||
                        rectangle.Left < contentLeft)
                    {
                        continue;
                    }

                    var label = SafeName(element).Trim();
                    if (label.Length is < 1 or > 80 || ContainsAny(label,
                            "搜索", "发送", "消息", "聊天记录", "语音", "视频", "最小化", "最大化", "关闭"))
                    {
                        continue;
                    }

                    labels.Add(label);
                }
                catch (ElementNotAvailableException) { }
            }

            return labels.Count == 1 ? labels.Single() : null;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            return null;
        }
    }

    private static OpenChatTarget FindOrOpenVerifiedChat(
        string requestedPlatform,
        string requestedContact)
    {
        var window = FindWindow(requestedPlatform);
        if (window is null)
        {
            return OpenChatTarget.Failed(new DynamicToolResult(
                false,
                $"没有发现已打开的{requestedPlatform}主窗口。请先打开或登录{requestedPlatform}；回天不会重复启动已有程序。 ",
                ResultCode: "not_ready"));
        }

        var topHandle = new IntPtr(window.Current.NativeWindowHandle);
        if (!ActivateWindowHandle(topHandle))
        {
            return OpenChatTarget.Failed(
                new DynamicToolResult(false, $"{requestedPlatform}窗口暂时无法切换到前台。 "));
        }

        // One initial snapshot serves both the current-chat shortcut and search
        // discovery.  The former is important for repeated sends: if the user is
        // already in the requested conversation there is no reason to search.
        var initialNodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var currentTarget = VerifyCurrentChatTarget(window, requestedContact, initialNodes);
        if (currentTarget.Verified)
        {
            return new OpenChatTarget(
                window,
                topHandle,
                requestedPlatform,
                Search: null,
                Snapshot: initialNodes,
                ContactLabel: requestedContact,
                Strategy: "当前会话快速路径",
                VerificationDetail: currentTarget.Detail,
                Failure: null);
        }

        var strategy = "无障碍搜索框";
        var search = FindChatSearchElement(window, initialNodes);
        if (search is null)
        {
            strategy = "聊天软件搜索快捷键";
            if (SendControlShortcut(VkF) == 0)
            {
                return OpenChatTarget.Failed(CaptureChatFailure(
                    requestedPlatform,
                    "没有找到联系人搜索框，搜索快捷键也没有生效。 "));
            }

            Thread.Sleep(150);
            search = GetFocusedElementWithin(window);
            if (search is not null)
            {
                try
                {
                    var focusedType = search.Current.ControlType;
                    if (focusedType != ControlType.Edit && focusedType != ControlType.Document)
                    {
                        search = null;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    search = null;
                }
            }
            if (search is null)
            {
                if (requestedPlatform.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
                    requestedPlatform.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
                    requestedPlatform.Contains("Weixin", StringComparison.OrdinalIgnoreCase))
                {
                    var preparation = PrepareWeixinGpuTargetWithOcr(
                        requestedContact,
                        topHandle);
                    if (preparation.Verified)
                    {
                        var verifiedNodes = window.FindAll(
                            TreeScope.Descendants,
                            Condition.TrueCondition);
                        return new OpenChatTarget(
                            window,
                            topHandle,
                            requestedPlatform,
                            Search: null,
                            Snapshot: verifiedNodes,
                            ContactLabel: preparation.VerifiedTitle ?? requestedContact,
                            Strategy: "微信本地文字识别快速路径",
                            VerificationDetail: preparation.Detail,
                            Failure: null);
                    }

                    return OpenChatTarget.Failed(CaptureChatFailure(
                        requestedPlatform,
                        preparation.Detail,
                        requiresVerification: false,
                        resultCode: preparation.ResultCode));
                }

                // WeChat 4.x renders its body on the GPU and commonly exposes
                // only the title bar to UIA.  Perform one bounded keyboard
                // search, open the first result but do not type a payload.  A
                // product-local visual check can verify the returned window and
                // continue through SendToVerifiedCurrentChat exactly once.
                var searchInput = TypeActive(requestedPlatform, requestedContact, replace: true);
                if (!searchInput.Success)
                {
                    return OpenChatTarget.Failed(CaptureChatFailure(
                        requestedPlatform,
                        "微信/QQ内部界面没有向系统开放，联系人搜索输入没有生效。 "));
                }

                Thread.Sleep(320);
                if (!SendReturnKey(topHandle))
                {
                    return OpenChatTarget.Failed(CaptureChatFailure(
                        requestedPlatform,
                        "联系人搜索已输入，但无法打开搜索结果。 "));
                }

                Thread.Sleep(340);
                return OpenChatTarget.Failed(CaptureChatFailure(
                    requestedPlatform,
                    $"已用快速搜索打开“{requestedContact}”的首个候选，但此版本客户端没有提供可读会话标题。" +
                    "本次尚未输入或发送任何内容；请只核对返回画面的当前会话标题，确认后再继续一次。",
                    requiresVerification: true,
                    resultCode: "target_verification_required"));
            }
        }

        var typedContact = InputTextIntoElement(window, search, requestedContact, replace: true);
        if (!typedContact.Success)
        {
            return OpenChatTarget.Failed(CaptureChatFailure(
                requestedPlatform,
                $"通过{strategy}输入联系人失败：{typedContact.Text}"));
        }

        Thread.Sleep(360);
        var resultNodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var candidates = FindChatContactCandidates(
            window,
            requestedContact,
            search,
            resultNodes);
        var strongCandidates = candidates
            .Where(candidate => candidate.Score >= 760)
            .ToArray();
        if (strongCandidates.Length != 1)
        {
            var suggestions = candidates
                .Select(candidate => candidate.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            var detail = suggestions.Length == 0
                ? "没有读取到联系人候选。可能是语音同音字、联系人名称不同，或客户端未向系统提供列表文字。"
                : "没有唯一匹配联系人。可能的同音或近似候选：" + string.Join("、", suggestions);
            return OpenChatTarget.Failed(CaptureChatFailure(
                requestedPlatform,
                detail + " 请让用户确认具体联系人，不能猜测发送对象。 ",
                resultCode: candidates.Count == 0 ? "not_found" : "ambiguous"));
        }

        var selected = strongCandidates[0];
        var contactClick = SelectChatCandidate(window, selected.Element);
        if (!contactClick.Success)
        {
            return OpenChatTarget.Failed(CaptureChatFailure(
                requestedPlatform,
                $"找到了联系人“{selected.Label}”，但没有成功进入会话：{contactClick.Text}"));
        }

        Thread.Sleep(300);
        var openNodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var targetVerification = VerifyOpenedChatTarget(
            window,
            requestedContact,
            selected,
            search,
            openNodes);
        if (!targetVerification.Verified)
        {
            return OpenChatTarget.Failed(CaptureChatFailure(
                requestedPlatform,
                $"已点击候选联系人“{selected.Label}”，但无法核对当前会话确实属于该联系人：{targetVerification.Detail} " +
                "为避免发错人，本次没有输入或发送消息；请根据返回画面确认联系人。",
                requiresVerification: true,
                resultCode: "target_verification_required"));
        }

        return new OpenChatTarget(
            window,
            topHandle,
            requestedPlatform,
            search,
            openNodes,
            selected.Label,
            strategy,
            targetVerification.Detail,
            Failure: null);
    }

    private static DynamicToolResult SendTextToOpenChat(
        OpenChatTarget target,
        string message)
    {
        var window = target.Window ?? throw new InvalidOperationException("聊天窗口不可用。");
        var snapshot = target.Snapshot ?? throw new InvalidOperationException("聊天窗口快照不可用。");
        var input = FindMessageInput(window, target.Search, snapshot);
        DynamicToolResult inputResult;
        int pointerX;
        int pointerY;
        if (input is not null)
        {
            var inputBounds = input.Current.BoundingRectangle;
            pointerX = checked((int)Math.Round(inputBounds.Left + inputBounds.Width / 2));
            pointerY = checked((int)Math.Round(inputBounds.Top + inputBounds.Height / 2));
            inputResult = InputTextIntoElement(window, input, message, replace: false);
        }
        else
        {
            (pointerX, pointerY) = GetChatInputPoint(window, target.TopHandle);
            var focus = PostVirtualClick(
                window,
                pointerX,
                pointerY,
                1,
                "已尝试聚焦聊天输入区域。 ");
            inputResult = focus.Success
                ? TypeActive(target.StrategyPlatform, message, replace: false)
                : focus;
        }

        if (!inputResult.Success)
        {
            return CaptureChatFailure(
                target.StrategyPlatform,
                $"已进入联系人“{target.ContactLabel}”，但文字没有可靠地进入消息框：{inputResult.Text}",
                pointerX,
                pointerY);
        }

        var visibleMessageCountBefore = CountVisibleMessageMatches(snapshot, message);
        var inputValueBefore = input is null ? null : TryReadEditableValue(input);
        if (!SendReturnKey(target.TopHandle))
        {
            return CaptureChatFailure(
                target.StrategyPlatform,
                "消息文字已经输入，但发送键没有生效；请观察返回画面后改用可见的发送按钮。 ",
                pointerX,
                pointerY);
        }

        Thread.Sleep(420);
        var afterNodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var visibleMessageCountAfter = CountVisibleMessageMatches(afterNodes, message);
        var inputValueAfter = input is null ? null : TryReadEditableValue(input);
        var inputCleared = inputValueBefore?.Contains(message, StringComparison.Ordinal) == true &&
                           string.IsNullOrWhiteSpace(inputValueAfter);
        var messageAppeared = visibleMessageCountAfter > visibleMessageCountBefore;
        var capture = CaptureScreen(target.StrategyPlatform);
        var evidence = messageAppeared
            ? "聊天记录中出现了本次消息文字"
            : inputCleared
                ? "消息输入框在发送后已清空"
                : string.Empty;
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            return new DynamicToolResult(
                true,
                $"已通过{target.Strategy}定位联系人“{target.ContactLabel}”，{target.VerificationDetail}；输入消息并发送；{evidence}。",
                capture.ImageDataUrl,
                pointerX,
                pointerY,
                ResultCode: "completed");
        }

        return new DynamicToolResult(
            true,
            $"已对联系人“{target.ContactLabel}”执行输入和发送，但这个客户端不提供可读取的发送结果。" +
            "请只观察返回的当前窗口画面，不要重复输入或重复发送。",
            capture.ImageDataUrl,
            pointerX,
            pointerY,
            RequiresVerification: true,
            ResultCode: "result_verification_required");
    }

    private static DynamicToolResult SendFilesToOpenChat(
        OpenChatTarget target,
        IReadOnlyList<string> files)
    {
        var window = target.Window ?? throw new InvalidOperationException("聊天窗口不可用。");
        var snapshot = target.Snapshot ?? throw new InvalidOperationException("聊天窗口快照不可用。");
        var input = FindMessageInput(window, target.Search, snapshot);
        int pointerX;
        int pointerY;
        bool focused;
        if (input is not null)
        {
            var bounds = input.Current.BoundingRectangle;
            pointerX = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
            pointerY = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
            focused = TryFocusForInput(window, input);
        }
        else
        {
            (pointerX, pointerY) = GetChatInputPoint(window, target.TopHandle);
            focused = PostVirtualClick(
                window,
                pointerX,
                pointerY,
                1,
                "已尝试聚焦聊天输入区域。 ").Success;
        }

        if (!focused || !IsForegroundWithinTarget(target.TopHandle))
        {
            return CaptureChatFailure(
                target.StrategyPlatform,
                "已进入目标会话，但文件发送区域没有可靠获得焦点，本次没有粘贴文件。 ",
                pointerX,
                pointerY);
        }

        var names = files
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        var countsBefore = names.ToDictionary(
            name => name,
            name => CountVisibleMessageMatches(snapshot, name),
            StringComparer.OrdinalIgnoreCase);
        var paste = TryPasteFilesPreservingClipboard(target.TopHandle, files);
        if (!paste.Dispatched)
        {
            return CaptureChatFailure(
                target.StrategyPlatform,
                "目标会话已打开，但文件没有成功进入待发送区域。 ",
                pointerX,
                pointerY);
        }

        Thread.Sleep(460);
        if (!SendReturnKey(target.TopHandle))
        {
            return CaptureChatFailure(
                target.StrategyPlatform,
                "文件已经放入待发送区域，但发送键没有生效；为避免重复，本次不会再次粘贴。 ",
                pointerX,
                pointerY,
                requiresVerification: true,
                resultCode: "result_verification_required");
        }

        Thread.Sleep(620);
        var afterNodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var fileAppeared = names.Any(name =>
            CountVisibleMessageMatches(afterNodes, name) > countsBefore[name]);
        var capture = CaptureScreen(target.StrategyPlatform);
        var fileLabel = names.Length == 1 ? names[0] : $"{names.Length} 个文件";
        if (fileAppeared)
        {
            return new DynamicToolResult(
                true,
                $"已通过{target.Strategy}定位联系人“{target.ContactLabel}”并发送文件“{fileLabel}”；聊天记录已出现文件名。",
                capture.ImageDataUrl,
                pointerX,
                pointerY,
                ResultCode: "completed");
        }

        return new DynamicToolResult(
            true,
            $"已向联系人“{target.ContactLabel}”粘贴并发送“{fileLabel}”，但这个客户端不提供可读取的文件发送结果。" +
            "请只核对返回画面，不要重复发送。",
            capture.ImageDataUrl,
            pointerX,
            pointerY,
            RequiresVerification: true,
            ResultCode: "result_verification_required");
    }

    private static string[] NormalizeExistingFilePaths(IReadOnlyList<string>? filePaths) =>
        (filePaths ?? [])
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path =>
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return string.Empty;
            }
        })
        .Where(path => path.Length > 0 && File.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .ToArray();

    private static (int X, int Y) GetChatInputPoint(
        AutomationElement window,
        IntPtr topHandle)
    {
        var bounds = window.Current.BoundingRectangle;
        var dpi = 96u;
        try
        {
            var actual = GetDpiForWindow(topHandle);
            if (actual > 0)
            {
                dpi = actual;
            }
        }
        catch
        {
        }

        // Weixin 4.x keeps the composer close to the bottom of the chat pane;
        // the previous 80%-height point landed in message history on tall
        // windows and was the direct cause of "clicked but never typed".
        var bottomOffset = Math.Max(64d, 60d * dpi / 96d);
        var x = checked((int)Math.Round(bounds.Left + bounds.Width * 0.60));
        var y = checked((int)Math.Round(bounds.Bottom - bottomOffset));
        return (x, y);
    }

    private static ChatTargetVerification VerifyCurrentChatTarget(
        AutomationElement window,
        string requestedContact,
        AutomationElementCollection nodes)
    {
        var windowBounds = window.Current.BoundingRectangle;
        var normalizedRequest = NormalizeComparableName(requestedContact);
        var headerBottom = windowBounds.Top + Math.Max(150, windowBounds.Height * 0.28);
        var contentLeft = windowBounds.Left + windowBounds.Width * 0.35;
        var matches = new List<string>();
        for (var index = 0; index < nodes.Count; index++)
        {
            var element = nodes[index];
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                if (current.IsOffscreen || !IsUsableBounds(bounds) ||
                    bounds.Top > headerBottom || bounds.Left < contentLeft)
                {
                    continue;
                }

                var label = SafeName(element).Trim();
                if (NormalizeComparableName(label).Equals(
                        normalizedRequest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(label);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        var unique = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return unique.Length == 1
            ? new ChatTargetVerification(true, $"当前会话标题已核对为“{unique[0]}”")
            : new ChatTargetVerification(false, unique.Length > 1
                ? "当前标题区域出现多个同名结果。"
                : "当前标题区域没有提供可读联系人名称。");
    }

    private static GpuTargetPreparation PrepareWeixinGpuTargetWithOcr(
        string contact,
        IntPtr windowHandle)
    {
        var scriptPath = FindWeixinOcrScript();
        if (scriptPath is null)
        {
            return new GpuTargetPreparation(
                false,
                "ocr_helper_missing",
                "当前微信版本没有开放内部控件，而且本地文字识别组件未随程序安装；本次没有发送。",
                null);
        }

        GpuTargetPreparation? preparation = null;
        var completed = false;
        var worker = new Thread(() =>
        {
            System.Windows.Forms.IDataObject? previousClipboard = null;
            var captured = false;
            try
            {
                captured = TryGetClipboardData(out previousClipboard);
                if (!captured)
                {
                    preparation = new GpuTargetPreparation(
                        false,
                        "clipboard_busy",
                        "系统剪贴板正被其他程序占用。为避免破坏用户内容，本次没有搜索或发送。",
                        null);
                    return;
                }

                preparation = RunWeixinOcrProcess(scriptPath, contact);
            }
            catch (Exception exception)
            {
                preparation = new GpuTargetPreparation(
                    false,
                    "ocr_failed",
                    $"微信联系人本地核对没有完成：{exception.Message} 本次没有发送。",
                    null);
            }
            finally
            {
                if (captured && !TryRestoreClipboardData(previousClipboard))
                {
                    preparation = new GpuTargetPreparation(
                        false,
                        "clipboard_restore_failed",
                        "联系人核对已停止，但系统剪贴板当时被其他程序占用，未能确认完整恢复；本次没有发送。",
                        null);
                }

                ReleaseWindowTopMost(windowHandle);
                completed = true;
            }
        })
        {
            IsBackground = true,
            Name = "HuiTian Weixin local OCR",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(11)) || !completed)
        {
            ReleaseWindowTopMost(windowHandle);
            return new GpuTargetPreparation(
                false,
                "ocr_timeout",
                "微信联系人核对超过了快速路径时限，已停止且没有发送。",
                null);
        }

        return preparation ?? new GpuTargetPreparation(
            false,
            "ocr_failed",
            "微信联系人核对没有返回结果，本次没有发送。",
            null);
    }

    private static GpuTargetPreparation RunWeixinOcrProcess(
        string scriptPath,
        string contact)
    {
        var tempRoot = Directory.Exists(@"D:\HuiTian\Cache")
            ? @"D:\HuiTian\Cache\Temp\WeixinOcr"
            : Path.Combine(Path.GetTempPath(), "HuiTian", "WeixinOcr");
        Directory.CreateDirectory(tempRoot);

        // Windows PowerShell 5 treats a UTF-8 script without BOM as the legacy
        // code page. Read it explicitly as UTF-8 inside a tiny ASCII wrapper,
        // then execute the resulting ScriptBlock with the contact in an
        // environment variable (never interpolate user text into a command).
        const string wrapper =
            "$ProgressPreference='SilentlyContinue';" +
            "$s=[IO.File]::ReadAllText($env:HUITIAN_WX_SCRIPT,[Text.UTF8Encoding]::new($false));" +
            "& ([ScriptBlock]::Create($s)) -Contact $env:HUITIAN_WX_CONTACT";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe",
            // Never compile the helper from the self-contained publish folder.
            // Windows PowerShell's Add-Type can otherwise probe the app's .NET
            // runtime assemblies and fail with misleading type-resolution errors.
            WorkingDirectory = tempRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        startInfo.Environment["HUITIAN_WX_SCRIPT"] = scriptPath;
        startInfo.Environment["HUITIAN_WX_CONTACT"] = contact;
        startInfo.Environment["TEMP"] = tempRoot;
        startInfo.Environment["TMP"] = tempRoot;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("无法启动 Windows 本地文字识别进程。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(9_200))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
            catch
            {
            }

            var timeoutStderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "helper terminated before stderr drained";
            WriteWeixinOcrDiagnostic("ocr_timeout", "", timeoutStderr, contact, -1);
            return new GpuTargetPreparation(false, "ocr_timeout",
                "微信联系人核对没有及时返回，已停止且没有发送；识别阶段耗时已记录。", null);
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        WriteWeixinOcrDiagnostic("helper_completed", "", stderr, contact, process.ExitCode);
        var jsonLine = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            WriteWeixinOcrDiagnostic("missing_json", stdout, stderr, contact, process.ExitCode);
            return new GpuTargetPreparation(
                false,
                "ocr_failed",
                "本机联系人识别组件没有正常启动，本次没有发送。",
                null);
        }

        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeValue)
            ? codeValue.GetString() ?? "ocr_failed"
            : "ocr_failed";
        var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
        var detailText = root.TryGetProperty("detail", out var detailValue)
            ? detailValue.GetString() ?? "微信联系人核对没有完成。"
            : "微信联系人核对没有完成。";
        var title = root.TryGetProperty("title", out var titleValue)
            ? titleValue.GetString()
            : null;
        if (ok && code.Equals("verified", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(title) &&
            NormalizeComparableName(title).Equals(
                NormalizeComparableName(contact), StringComparison.OrdinalIgnoreCase))
        {
            return new GpuTargetPreparation(
                true,
                "verified",
                $"本地文字识别已核对搜索结果和当前聊天标题为“{title ?? contact}”",
                title ?? contact);
        }

        if (ok && code.Equals("verified", StringComparison.OrdinalIgnoreCase))
        {
            return new GpuTargetPreparation(false, "title_mismatch",
                "搜索结果虽已打开，但会话标题与指定联系人并非完全一致；本次没有发送。", title);
        }

        return new GpuTargetPreparation(false, code, detailText, title);
    }

    private static void WriteWeixinOcrDiagnostic(
        string code,
        string stdout,
        string stderr,
        string contact,
        int exitCode)
    {
        try
        {
            var logRoot = Directory.Exists(@"D:\HuiTian")
                ? @"D:\HuiTian\Cache\Logs"
                : Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logRoot);
            var safeStderr = RedactWeixinOcrDiagnostic(stderr, contact);
            var entry = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now,
                code,
                exitCode,
                // OCR stdout can contain recognized contact names. Keep only
                // its size; the technical error stream is sufficient to
                // diagnose helper startup/compilation failures.
                stdoutLength = stdout?.Length ?? 0,
                stderr = safeStderr,
            });
            lock (WeixinOcrDiagnosticGate)
            {
                File.AppendAllText(
                    Path.Combine(logRoot, "weixin-ocr-helper.jsonl"),
                    entry + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never change the user-visible result.
        }
    }

    internal static string RedactWeixinOcrDiagnostic(string value, string contact)
    {
        var redacted = value ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(contact))
        {
            redacted = redacted.Replace(contact, "[联系人已隐去]", StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    private static string? FindWeixinOcrScript()
    {
        const string relative = @"vendor\weixin-fast-send\weixin_verify_target.ps1";
        var direct = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(direct))
        {
            return direct;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 9; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var workingCandidate = Path.Combine(Directory.GetCurrentDirectory(), relative);
        return File.Exists(workingCandidate) ? workingCandidate : null;
    }

    private static void ReleaseWindowTopMost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        const uint flags = 0x0001 | 0x0002 | 0x0040;
        SetWindowPos(windowHandle, new IntPtr(-2), 0, 0, 0, 0, flags);
    }

    private static AutomationElement? FindChatSearchElement(
        AutomationElement window,
        AutomationElementCollection nodes)
    {
        var windowBounds = window.Current.BoundingRectangle;
        AutomationElement? best = null;
        var bestScore = double.MinValue;
        for (var index = 0; index < nodes.Count; index++)
        {
            var element = nodes[index];
            try
            {
                var current = element.Current;
                if (current.ControlType is not ({ } type) ||
                    (type != ControlType.Edit && type != ControlType.Document) ||
                    !current.IsEnabled || current.IsOffscreen ||
                    !IsUsableBounds(current.BoundingRectangle))
                {
                    continue;
                }

                var label = (SafeName(element) + " " + SafeAutomationId(element)).Trim();
                var searchNamed = ContainsAny(
                    label,
                    "搜索", "查找", "search", "find", "联系人", "contact");
                if (!searchNamed)
                {
                    continue;
                }

                var bounds = current.BoundingRectangle;
                var verticalRatio = windowBounds.Height <= 0
                    ? 1
                    : Math.Clamp((bounds.Top - windowBounds.Top) / windowBounds.Height, 0, 1);
                var horizontalRatio = windowBounds.Width <= 0
                    ? 1
                    : Math.Clamp((bounds.Left - windowBounds.Left) / windowBounds.Width, 0, 1);
                var score = 500 - verticalRatio * 180 - horizontalRatio * 45;
                if (label.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("search", StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return best;
    }

    private static IReadOnlyList<ChatContactCandidate> FindChatContactCandidates(
        AutomationElement window,
        string requestedContact,
        AutomationElement search,
        AutomationElementCollection nodes)
    {
        var windowBounds = window.Current.BoundingRectangle;
        var searchBounds = search.Current.BoundingRectangle;
        var matches = new List<ChatContactCandidate>();
        for (var index = 0; index < nodes.Count; index++)
        {
            var element = nodes[index];
            try
            {
                if (IsSameElement(element, search))
                {
                    continue;
                }

                var candidateElement = ResolveChatCandidateElement(element, window);
                var current = candidateElement.Current;
                if (!current.IsEnabled || current.IsOffscreen ||
                    current.ControlType is null ||
                    current.ControlType == ControlType.Edit ||
                    current.ControlType == ControlType.Document ||
                    !IsUsableBounds(current.BoundingRectangle) ||
                    !IsLikelyChatSearchResultBounds(
                        current.BoundingRectangle,
                        searchBounds,
                        windowBounds) ||
                    HasChatHistoryContext(candidateElement, window))
                {
                    continue;
                }

                var label = SafeName(candidateElement);
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = SafeName(element);
                }

                if (string.IsNullOrWhiteSpace(label) || label.Length > 80 ||
                    ContainsAny(
                        label,
                        "搜索", "Search", "聊天记录", "消息记录", "会话内容", "发送消息"))
                {
                    continue;
                }

                var semanticScore = ScoreContactCandidate(requestedContact, label);
                if (semanticScore < 300)
                {
                    continue;
                }

                // Geometry/control-type boosts rank rows that express the same
                // contact; they must never promote a typo or homophone from a
                // confirmation suggestion into an automatic recipient choice.
                var maySelectAutomatically = semanticScore >= 760;
                var score = semanticScore;

                if (current.ControlType == ControlType.ListItem ||
                    current.ControlType == ControlType.DataItem)
                {
                    score += 90;
                }
                else if (current.ControlType == ControlType.Button)
                {
                    score += 55;
                }

                if (!maySelectAutomatically)
                {
                    score = Math.Min(score, 759);
                }

                matches.Add(new ChatContactCandidate(
                    candidateElement,
                    label.Trim(),
                    score,
                    current.BoundingRectangle));
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return matches
            .OrderByDescending(candidate => candidate.Score)
            // UI Automation often exposes both a result row and a text child.
            // Collapse only elements occupying the same row. Two distinct rows
            // with the same display name remain ambiguous and must not be sent to.
            .GroupBy(
                candidate => BuildChatCandidateGeometryKey(candidate),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .ToArray();
    }

    internal static double ScoreContactCandidate(string requested, string candidate)
    {
        var phoneticRequested = NormalizeComparableName(ExtractPrimaryChatName(requested));
        var phoneticCandidate = NormalizeComparableName(ExtractPrimaryChatName(candidate));
        requested = NormalizeComparableName(requested);
        candidate = NormalizeComparableName(candidate);
        if (requested.Length == 0 || candidate.Length == 0)
        {
            return 0;
        }

        if (candidate.Equals(requested, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ||
            requested.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 850;
        }

        if (candidate.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
            requested.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 780;
        }

        // Common Chinese name readings are deliberately a suggestion signal,
        // never a send signal. A phonetic match scores below the 760 automatic
        // selection threshold so “良宇” may suggest “凉雨” without guessing.
        if (AreLikelyChineseHomophones(phoneticRequested, phoneticCandidate))
        {
            return 735;
        }

        var distance = LevenshteinDistance(requested, candidate);
        var maximumLength = Math.Max(requested.Length, candidate.Length);
        if (distance == 1)
        {
            return maximumLength <= 3 ? 690 : 610;
        }

        if (distance == 2 && maximumLength >= 4)
        {
            return 510;
        }

        var sharedCharacters = requested
            .Where(character => candidate.Contains(character))
            .Distinct()
            .Count();
        return sharedCharacters == 0
            ? 0
            : 300 + 180d * sharedCharacters / Math.Max(1, requested.Distinct().Count());
    }

    private static string BuildChatCandidateGeometryKey(ChatContactCandidate candidate)
    {
        var bounds = candidate.Bounds;
        return string.Join(
            '|',
            NormalizeComparableName(candidate.Label),
            Math.Round(bounds.Left),
            Math.Round(bounds.Top),
            Math.Round(bounds.Width),
            Math.Round(bounds.Height));
    }

    private static string ExtractPrimaryChatName(string value)
    {
        var separatorIndex = value.IndexOfAny(['（', '(', '【', '[', '·', '-', '—']);
        return separatorIndex <= 0 ? value : value[..separatorIndex];
    }

    private static AutomationElement ResolveChatCandidateElement(
        AutomationElement element,
        AutomationElement window)
    {
        var current = element;
        for (var depth = 0; depth < 4; depth++)
        {
            try
            {
                var controlType = current.Current.ControlType;
                if (controlType == ControlType.ListItem ||
                    controlType == ControlType.DataItem ||
                    controlType == ControlType.Button)
                {
                    return current;
                }

                var parent = TreeWalker.ControlViewWalker.GetParent(current);
                if (parent is null || IsSameElement(parent, window))
                {
                    break;
                }

                current = parent;
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
        }

        return element;
    }

    internal static bool IsLikelyChatSearchResultBounds(
        System.Windows.Rect candidateBounds,
        System.Windows.Rect searchBounds,
        System.Windows.Rect windowBounds)
    {
        if (!IsUsableBounds(candidateBounds) ||
            !IsUsableBounds(searchBounds) ||
            !IsUsableBounds(windowBounds))
        {
            return false;
        }

        // Search results are expected below the search box and in the same
        // navigation column/pop-over. This excludes identical names inside the
        // conversation history, which normally occupies the main content pane.
        var candidateCenterX = candidateBounds.Left + candidateBounds.Width / 2;
        var horizontalPadding = Math.Max(72, searchBounds.Width * 0.32);
        var sameColumn = candidateCenterX >= searchBounds.Left - horizontalPadding &&
                         candidateCenterX <= searchBounds.Right + horizontalPadding;
        // Some Win32/UIA providers report edit controls in logical pixels but
        // list rows in physical pixels on a scaled display.  In that case a
        // genuine result appears immediately to the right of the reported
        // search bounds instead of directly below it.  Accept only a compact,
        // nearby pop-over; a same-name row deeper in the conversation remains
        // outside this band and is therefore ignored.
        var adjacentPopover =
            candidateBounds.Left >= searchBounds.Right - 12 &&
            candidateBounds.Left <= searchBounds.Right + Math.Max(96, searchBounds.Width * 0.55) &&
            candidateBounds.Width <= Math.Max(windowBounds.Width * 0.55, searchBounds.Width * 1.8);
        var belowSearch = candidateBounds.Top >= searchBounds.Bottom - 12;
        var verticallyNearSearch = candidateBounds.Top <=
                                   searchBounds.Bottom + Math.Max(120, searchBounds.Height * 5);
        var withinWindow = candidateBounds.Left >= windowBounds.Left - 2 &&
                           candidateBounds.Right <= windowBounds.Right + 2 &&
                           candidateBounds.Top >= windowBounds.Top - 2 &&
                           candidateBounds.Bottom <= windowBounds.Bottom + 2;
        return (sameColumn || adjacentPopover) && belowSearch &&
               verticallyNearSearch && withinWindow;
    }

    private static bool HasChatHistoryContext(
        AutomationElement element,
        AutomationElement window)
    {
        var current = element;
        for (var depth = 0; depth < 6; depth++)
        {
            try
            {
                var context = (SafeName(current) + " " + SafeAutomationId(current)).Trim();
                if (ContainsAny(
                        context,
                        "聊天记录", "消息记录", "会话内容", "conversation history", "message history"))
                {
                    return true;
                }

                var parent = TreeWalker.ControlViewWalker.GetParent(current);
                if (parent is null || IsSameElement(parent, window))
                {
                    return false;
                }

                current = parent;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        return false;
    }

    internal static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string NormalizeComparableName(string value) =>
        new(value
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    internal static bool AreLikelyChineseHomophones(string left, string right)
    {
        var normalizedLeft = NormalizeComparableName(left);
        var normalizedRight = NormalizeComparableName(right);
        if (normalizedLeft.Length == 0 ||
            normalizedRight.Length == 0 ||
            normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
            normalizedLeft.Length != normalizedRight.Length)
        {
            return false;
        }

        var transformedDifferentCharacter = false;
        for (var index = 0; index < normalizedLeft.Length; index++)
        {
            var leftCharacter = normalizedLeft[index];
            var rightCharacter = normalizedRight[index];
            if (leftCharacter == rightCharacter)
            {
                continue;
            }

            if (!CommonChineseNameReadings.TryGetValue(leftCharacter, out var leftReading) ||
                !CommonChineseNameReadings.TryGetValue(rightCharacter, out var rightReading) ||
                !leftReading.Equals(rightReading, StringComparison.Ordinal))
            {
                return false;
            }

            transformedDifferentCharacter = true;
        }

        return transformedDifferentCharacter;
    }

    private static ChatTargetVerification VerifyOpenedChatTarget(
        AutomationElement window,
        string requestedContact,
        ChatContactCandidate selected,
        AutomationElement search,
        AutomationElementCollection nodes)
    {
        var windowBounds = window.Current.BoundingRectangle;
        var searchBounds = search.Current.BoundingRectangle;
        var normalizedRequest = NormalizeComparableName(requestedContact);
        var headerBottom = windowBounds.Top + Math.Max(150, windowBounds.Height * 0.28);
        var resultColumnRight = searchBounds.Right + Math.Max(72, searchBounds.Width * 0.32);
        var headerEvidence = new List<(string Label, System.Windows.Rect Bounds)>();
        for (var index = 0; index < nodes.Count; index++)
        {
            var element = nodes[index];
            try
            {
                if (IsSameElement(element, search))
                {
                    continue;
                }

                var current = element.Current;
                var bounds = current.BoundingRectangle;
                if (current.IsOffscreen ||
                    !IsUsableBounds(bounds) ||
                    bounds.Top > headerBottom ||
                    bounds.Left < resultColumnRight ||
                    IsLikelyChatSearchResultBounds(bounds, searchBounds, windowBounds))
                {
                    continue;
                }

                var label = SafeName(element).Trim();
                var normalizedLabel = NormalizeComparableName(label);
                if (normalizedLabel.Length == 0 ||
                    !normalizedLabel.Equals(normalizedRequest, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                headerEvidence.Add((label, bounds));
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        var uniqueHeaders = headerEvidence
            .GroupBy(
                value => string.Join(
                    '|',
                    NormalizeComparableName(value.Label),
                    Math.Round(value.Bounds.Left),
                    Math.Round(value.Bounds.Top),
                    Math.Round(value.Bounds.Width),
                    Math.Round(value.Bounds.Height)),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (uniqueHeaders.Length == 1)
        {
            return new ChatTargetVerification(
                true,
                $"当前会话标题已核对为“{uniqueHeaders[0].Label}”");
        }

        // Some clients expose no separate title but do expose a uniquely
        // selected search-result row. This remains visible, inspectable target
        // evidence and is safer than inferring from editor focus.
        try
        {
            if (uniqueHeaders.Length == 0 &&
                NormalizeComparableName(selected.Label).Equals(
                    normalizedRequest, StringComparison.OrdinalIgnoreCase) &&
                selected.Element.TryGetCurrentPattern(
                    SelectionItemPattern.Pattern,
                    out var selectionObject) &&
                selectionObject is SelectionItemPattern selectionPattern &&
                selectionPattern.Current.IsSelected)
            {
                return new ChatTargetVerification(
                    true,
                    $"联系人结果“{selected.Label}”处于唯一选中状态");
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return uniqueHeaders.Length > 1
            ? new ChatTargetVerification(false, "发现多个相同的会话标题，目标仍有歧义。")
            : new ChatTargetVerification(false, "当前窗口没有提供唯一、可读的联系人标题或选中状态。");
    }

    private static DynamicToolResult SelectChatCandidate(
        AutomationElement window,
        AutomationElement candidate)
    {
        if (candidate.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject))
        {
            ((SelectionItemPattern)selectionObject).Select();
            return new DynamicToolResult(true, $"已选择联系人：{SafeName(candidate)}");
        }

        if (candidate.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject))
        {
            ((InvokePattern)invokeObject).Invoke();
            return new DynamicToolResult(true, $"已打开联系人：{SafeName(candidate)}");
        }

        var bounds = candidate.Current.BoundingRectangle;
        if (!IsUsableBounds(bounds))
        {
            return new DynamicToolResult(false, "联系人没有可点击的位置。 ");
        }

        var x = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
        var y = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
        return PostVirtualClick(
            window,
            x,
            y,
            1,
            $"已单击联系人：{SafeName(candidate)}");
    }

    private static AutomationElement? FindMessageInput(
        AutomationElement window,
        AutomationElement? search,
        AutomationElementCollection nodes)
    {
        var windowBounds = window.Current.BoundingRectangle;
        AutomationElement? best = null;
        var bestScore = double.MinValue;
        for (var index = 0; index < nodes.Count; index++)
        {
            var candidate = nodes[index];
            if ((search is not null && IsSameElement(candidate, search)) ||
                !TryScoreEditableCandidate(candidate, windowBounds, out var score))
            {
                continue;
            }

            var label = (SafeName(candidate) + " " + SafeAutomationId(candidate)).Trim();
            if (ContainsAny(label, "搜索", "查找", "search", "find"))
            {
                continue;
            }

            if (ContainsAny(label, "消息", "输入", "编辑", "message", "chat"))
            {
                score += 100;
            }

            var bounds = candidate.Current.BoundingRectangle;
            if (windowBounds.Width > 0)
            {
                var leftRatio = Math.Clamp(
                    (bounds.Left - windowBounds.Left) / windowBounds.Width,
                    0,
                    1);
                score += leftRatio * 65;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static string? TryReadEditableValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) &&
                textObject is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(20_100).TrimEnd('\0');
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private static int CountVisibleMessageMatches(
        AutomationElementCollection nodes,
        string message)
    {
        var count = 0;
        for (var index = 0; index < nodes.Count; index++)
        {
            var element = nodes[index];
            try
            {
                var current = element.Current;
                if (current.IsOffscreen ||
                    current.ControlType == ControlType.Edit ||
                    current.ControlType == ControlType.Document)
                {
                    continue;
                }

                var label = SafeName(element);
                if (label.Equals(message, StringComparison.Ordinal) ||
                    (message.Length >= 6 && label.Contains(message, StringComparison.Ordinal)))
                {
                    count++;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return count;
    }

    private static bool SendReturnKey(IntPtr topHandle)
    {
        if (!IsForegroundWithinTarget(topHandle))
        {
            return false;
        }

        var inputs = new[]
        {
            CreateVirtualKeyInput(VkReturn, keyUp: false),
            CreateVirtualKeyInput(VkReturn, keyUp: true),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
    }

    private static DynamicToolResult CaptureChatFailure(
        string platform,
        string text,
        int? pointerX = null,
        int? pointerY = null,
        bool requiresVerification = false,
        string? resultCode = null)
    {
        var capture = CaptureScreen(platform);
        return new DynamicToolResult(
            false,
            text,
            capture.Success ? capture.ImageDataUrl : null,
            pointerX,
            pointerY,
            RequiresVerification: requiresVerification,
            ResultCode: resultCode);
    }

    private static bool IsSameElement(AutomationElement left, AutomationElement right)
    {
        try
        {
            return Automation.Compare(left, right);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    public static DynamicToolResult Scroll(string? windowTitle, string direction, int amount)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        ActivateWindowHandle(new IntPtr(window.Current.NativeWindowHandle));
        var normalizedDirection = direction.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? 1
            : -1;
        var bounds = window.Current.BoundingRectangle;
        var x = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
        var y = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
        var wheelDelta = normalizedDirection * 120 * Math.Clamp(amount, 1, 10);
        var topHandle = new IntPtr(window.Current.NativeWindowHandle);
        var target = ResolveTargetWindowAtPoint(topHandle, x, y);
        if (target == IntPtr.Zero)
        {
            return new DynamicToolResult(false, "没有在目标窗口内找到可滚动区域。 ");
        }

        var wParam = new IntPtr(wheelDelta << 16);
        // WM_MOUSEWHEEL carries screen coordinates, unlike button messages.
        var lParam = MakeLong(x, y);
        if (!PostMessage(target, WmMouseWheel, wParam, lParam))
        {
            return new DynamicToolResult(false, "目标窗口没有接受滚动操作。 ");
        }
        return new DynamicToolResult(
            true,
            normalizedDirection > 0 ? "已向上滚动。" : "已向下滚动。 ",
            PointerX: x,
            PointerY: y);
    }

    public static DynamicToolResult CaptureScreen(string? windowTitle)
    {
        try
        {
            System.Drawing.Rectangle bounds;
            var label = "桌面";
            var windowHandle = IntPtr.Zero;
            var isDesktopCapture = string.IsNullOrWhiteSpace(windowTitle);
            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                var window = FindWindow(windowTitle);
                if (window is null)
                {
                    return new DynamicToolResult(false, "没有找到要观察的窗口。 ");
                }

                windowHandle = new IntPtr(window.Current.NativeWindowHandle);
                ActivateWindowHandle(windowHandle);
                Thread.Sleep(180);
                var rectangle = window.Current.BoundingRectangle;
                bounds = System.Drawing.Rectangle.FromLTRB(
                    checked((int)Math.Floor(rectangle.Left)),
                    checked((int)Math.Floor(rectangle.Top)),
                    checked((int)Math.Ceiling(rectangle.Right)),
                    checked((int)Math.Ceiling(rectangle.Bottom)));
                label = SafeName(window);
            }
            else
            {
                bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            }

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return new DynamicToolResult(false, "当前屏幕区域无法读取。 ");
            }

            using var bitmap = new System.Drawing.Bitmap(
                bounds.Width,
                bounds.Height,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left,
                    bounds.Top,
                    0,
                    0,
                    bitmap.Size,
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var scale = Math.Min(
                1d,
                Math.Min(
                    MaxCaptureWidth / (double)bounds.Width,
                    MaxCaptureHeight / (double)bounds.Height));
            var imageWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var imageHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            System.Drawing.Bitmap? resized = null;
            try
            {
                var imageToEncode = bitmap;
                if (imageWidth != bounds.Width || imageHeight != bounds.Height)
                {
                    resized = new System.Drawing.Bitmap(
                        imageWidth,
                        imageHeight,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using var resizeGraphics = System.Drawing.Graphics.FromImage(resized);
                    resizeGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    resizeGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    resizeGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    resizeGraphics.DrawImage(
                        bitmap,
                        new System.Drawing.Rectangle(0, 0, imageWidth, imageHeight),
                        new System.Drawing.Rectangle(0, 0, bounds.Width, bounds.Height),
                        System.Drawing.GraphicsUnit.Pixel);
                    imageToEncode = resized;
                }

                using var stream = new MemoryStream();
                var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .First(codec => codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var jpegSettings = new System.Drawing.Imaging.EncoderParameters(1);
                jpegSettings.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality,
                    78L);
                imageToEncode.Save(stream, jpegCodec, jpegSettings);
                var encodedBytes = stream.ToArray();
                var contentHash = Convert.ToHexString(SHA256.HashData(encodedBytes));
                var duplicate = IsDuplicateCapture(
                    windowHandle,
                    isDesktopCapture,
                    contentHash,
                    TimeSpan.FromSeconds(15));
                RememberCaptureMap(new ScreenCaptureMap(
                    windowHandle,
                    isDesktopCapture,
                    imageWidth,
                    imageHeight,
                    bounds,
                    DateTimeOffset.UtcNow,
                    contentHash));
                if (duplicate)
                {
                    return new DynamicToolResult(
                        true,
                        $"“{label}”与 15 秒内的上一张画面完全相同，未重复附加图像。请换用具名控件、键盘路径或其他策略，不要继续截取同一画面。");
                }

                var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(encodedBytes);
                return new DynamicToolResult(
                    true,
                    $"已观察“{label}”。返回图像坐标为 {imageWidth}×{imageHeight}；后续点击的 x、y 必须直接使用这张图中的坐标，回天会自动换算显示缩放和多屏偏移。",
                    dataUrl);
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"暂时无法观察屏幕：{exception.Message}");
        }
    }

    public static DynamicToolResult ClickPoint(
        string? windowTitle,
        int x,
        int y,
        bool direct,
        int clickCount = 1)
    {
        if (x < 0 || y < 0)
        {
            return new DynamicToolResult(false, "点击坐标必须是非负数。 ");
        }

        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return ClickDesktopPoint(x, y, direct, clickCount);
        }

        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        var bounds = window.Current.BoundingRectangle;
        var topHandle = new IntPtr(window.Current.NativeWindowHandle);
        var physicalBounds = System.Drawing.Rectangle.FromLTRB(
            checked((int)Math.Floor(bounds.Left)),
            checked((int)Math.Floor(bounds.Top)),
            checked((int)Math.Ceiling(bounds.Right)),
            checked((int)Math.Ceiling(bounds.Bottom)));
        var captureMap = GetCaptureMap(topHandle, isDesktop: false);
        if (captureMap is null || captureMap.PhysicalBounds.Size != physicalBounds.Size)
            return new DynamicToolResult(false, "缺少目标窗口的有效画面，或窗口大小已改变。请重新观察后再定位，未执行点击。", ResultCode: "fresh_observation_required");
        var coordinateWidth = captureMap?.ImageWidth ?? physicalBounds.Width;
        var coordinateHeight = captureMap?.ImageHeight ?? physicalBounds.Height;
        if (x >= coordinateWidth || y >= coordinateHeight)
        {
            return new DynamicToolResult(
                false,
                $"点击坐标超出了最新窗口画面（{coordinateWidth}×{coordinateHeight}），请重新观察窗口。 ");
        }

        var (screenX, screenY) = MapCapturedPoint(
            x,
            y,
            coordinateWidth,
            coordinateHeight,
            physicalBounds);
        clickCount = clickCount == 2 ? 2 : 1;
        if (direct)
        {
            if (!ActivateWindowHandle(topHandle))
            {
                return new DynamicToolResult(false, "目标窗口暂时无法切换到前台，未执行直接点击。 ");
            }
            SendDirectClick(screenX, screenY, clickCount);
            return new DynamicToolResult(
                true,
                $"已在“{SafeName(window)}”中直接{(clickCount == 2 ? "双击" : "单击")}指定位置。请重新观察确认结果。 ",
                PointerX: screenX,
                PointerY: screenY,
                RequiresVerification: true);
        }

        return PostVirtualClick(
            window,
            screenX,
            screenY,
            clickCount,
            $"已在“{SafeName(window)}”中{(clickCount == 2 ? "双击" : "单击")}指定位置。请重新观察确认结果。 ");
    }

    private static DynamicToolResult ClickDesktopPoint(
        int x,
        int y,
        bool direct,
        int clickCount)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var captureMap = GetCaptureMap(IntPtr.Zero, isDesktop: true);
        if (captureMap is null || captureMap.PhysicalBounds != bounds)
            return new DynamicToolResult(false, "缺少有效桌面画面，或显示器布局已改变。请重新观察后再定位，未执行点击。", ResultCode: "fresh_observation_required");
        var coordinateWidth = captureMap?.ImageWidth ?? bounds.Width;
        var coordinateHeight = captureMap?.ImageHeight ?? bounds.Height;
        if (x >= coordinateWidth || y >= coordinateHeight)
        {
            return new DynamicToolResult(
                false,
                $"点击坐标超出了最新桌面画面（{coordinateWidth}×{coordinateHeight}），请重新观察桌面。 ");
        }

        var (screenX, screenY) = MapCapturedPoint(
            x,
            y,
            coordinateWidth,
            coordinateHeight,
            bounds);
        clickCount = clickCount == 2 ? 2 : 1;
        if (direct)
        {
            SendDirectClick(screenX, screenY, clickCount);
            return new DynamicToolResult(
                true,
                $"已在桌面画面中直接{(clickCount == 2 ? "双击" : "单击")}指定位置。请重新观察确认结果。 ",
                PointerX: screenX,
                PointerY: screenY,
                RequiresVerification: true);
        }

        var topHandle = FindTopLevelWindowAtPoint(screenX, screenY);
        if (topHandle == IntPtr.Zero)
        {
            return new DynamicToolResult(false, "没有在桌面指定位置找到可点击窗口。 ");
        }

        return PostVirtualClick(
            topHandle,
            screenX,
            screenY,
            clickCount,
            $"已在桌面画面中{(clickCount == 2 ? "双击" : "单击")}指定位置。请重新观察确认结果。 ",
            requireActivation: false);
    }

    internal static (int X, int Y) MapCapturedPoint(
        int imageX,
        int imageY,
        int imageWidth,
        int imageHeight,
        System.Drawing.Rectangle physicalBounds)
    {
        if (imageWidth <= 0 || imageHeight <= 0 ||
            imageX < 0 || imageY < 0 || imageX >= imageWidth || imageY >= imageHeight ||
            physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageX));
        }

        // Map pixel centres rather than pixel edges. This makes identity maps
        // exact and keeps the first/last returned-image pixels inside the
        // physical desktop even with fractional DPI scale factors.
        var physicalX = physicalBounds.Left +
                        (imageX + 0.5d) * physicalBounds.Width / imageWidth - 0.5d;
        var physicalY = physicalBounds.Top +
                        (imageY + 0.5d) * physicalBounds.Height / imageHeight - 0.5d;
        return (
            Math.Clamp(
                checked((int)Math.Round(physicalX, MidpointRounding.AwayFromZero)),
                physicalBounds.Left,
                physicalBounds.Right - 1),
            Math.Clamp(
                checked((int)Math.Round(physicalY, MidpointRounding.AwayFromZero)),
                physicalBounds.Top,
                physicalBounds.Bottom - 1));
    }

    private static void RememberCaptureMap(ScreenCaptureMap map)
    {
        lock (CaptureMapSync)
        {
            _lastCaptureMap = map;
        }
    }

    internal static void ResetCaptureMap()
    {
        lock (CaptureMapSync) _lastCaptureMap = null;
    }

    private static bool IsDuplicateCapture(
        IntPtr windowHandle,
        bool isDesktop,
        string contentHash,
        TimeSpan maximumAge)
    {
        lock (CaptureMapSync)
        {
            var map = _lastCaptureMap;
            return map is not null &&
                   map.IsDesktop == isDesktop &&
                   (isDesktop || map.WindowHandle == windowHandle) &&
                   DateTimeOffset.UtcNow - map.CapturedAt <= maximumAge &&
                   string.Equals(map.ContentHash, contentHash, StringComparison.Ordinal);
        }
    }

    private static ScreenCaptureMap? GetCaptureMap(IntPtr windowHandle, bool isDesktop)
    {
        lock (CaptureMapSync)
        {
            var map = _lastCaptureMap;
            if (map is null ||
                map.IsDesktop != isDesktop ||
                (!isDesktop && map.WindowHandle != windowHandle) ||
                DateTimeOffset.UtcNow - map.CapturedAt > TimeSpan.FromMinutes(2))
            {
                return null;
            }

            return map;
        }
    }

    private static void SendDirectClick(int screenX, int screenY, int clickCount)
    {
        SetCursorPos(screenX, screenY);
        for (var click = 0; click < clickCount; click++)
        {
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            if (click + 1 < clickCount)
            {
                Thread.Sleep(Math.Min(110, (int)GetDoubleClickTime() / 3));
            }
        }
    }

    public static DynamicToolResult TypeActive(
        string? windowTitle,
        string? text,
        bool replace)
    {
        if (text is null)
        {
            return new DynamicToolResult(false, "没有提供要输入的文字。 ");
        }

        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        var topHandle = new IntPtr(window.Current.NativeWindowHandle);
        if (!ActivateWindowHandle(topHandle))
        {
            return new DynamicToolResult(false, "目标窗口暂时无法切换到前台，为避免输入到错误位置，本次没有输入。 ");
        }
        Thread.Sleep(90);
        var focused = GetFocusedElementWithin(window);
        var nativeFocus = GetNativeFocusedHandle(topHandle);
        if (focused is null && nativeFocus == IntPtr.Zero)
        {
            return new DynamicToolResult(false, "当前焦点不在目标窗口的输入区域。请先重新观察并点击输入框。 ");
        }

        if (focused is not null &&
            focused.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValuePattern) &&
            rawValuePattern is ValuePattern valuePattern)
        {
            if (valuePattern.Current.IsReadOnly)
            {
                return new DynamicToolResult(false, "当前输入区域是只读的。 ");
            }

            var expected = replace ? text : valuePattern.Current.Value + text;
            valuePattern.SetValue(expected);
            Thread.Sleep(70);
            var actual = valuePattern.Current.Value ?? string.Empty;
            var verified = replace
                ? string.Equals(actual, expected, StringComparison.Ordinal)
                : actual.EndsWith(text, StringComparison.Ordinal);
            return verified
                ? new DynamicToolResult(true, $"已向“{SafeName(window)}”的当前输入位置输入并复核文字。 ")
                : new DynamicToolResult(false, "输入后复核不一致。请重新观察输入框，不能把这一步当作成功。 ");
        }

        if (replace)
        {
            var selectionDispatch = SendControlShortcut(VkA);
            if (selectionDispatch == 0)
            {
                return new DynamicToolResult(false, "当前输入区域无法执行全选，未继续替换原文字。 ");
            }
        }

        var dispatch = DispatchTextToFocusedControl(topHandle, nativeFocus, text);
        if (!dispatch.Dispatched)
        {
            return new DynamicToolResult(false, "当前输入区域没有接受键盘输入。请重新观察输入框后再试。 ");
        }

        Thread.Sleep(80);
        var verification = VerifyFocusedValue(text, replace);
        return verification switch
        {
            true when dispatch.ClipboardRestored => new DynamicToolResult(true, $"已向“{SafeName(window)}”的当前输入位置输入并复核文字。 "),
            true => new DynamicToolResult(true, $"已向“{SafeName(window)}”输入并复核文字；系统剪贴板当时被其他程序占用，未能确认已完整恢复。 "),
            false => new DynamicToolResult(false, "输入后复核不一致。请重新观察输入框，不能把这一步当作成功。 "),
            null when dispatch.ClipboardRestored => new DynamicToolResult(
                true,
                $"已向“{SafeName(window)}”发送输入动作，但该应用不提供可读取的输入值。必须立即重新观察屏幕；看到文字前不得声称输入成功。 ",
                RequiresVerification: true),
            null => new DynamicToolResult(
                true,
                $"已向“{SafeName(window)}”发送输入动作，但该应用不提供可读取的输入值，且系统剪贴板当时被其他程序占用。必须立即重新观察屏幕，并确认剪贴板内容。 ",
                RequiresVerification: true),
        };
    }

    public static DynamicToolResult PressKeys(string? windowTitle, string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return new DynamicToolResult(false, "没有提供按键。 ");
        }

        var window = FindWindow(windowTitle);
        if (window is null)
        {
            return new DynamicToolResult(false, "没有找到目标窗口。 ");
        }

        var sequence = keys.Trim().ToUpperInvariant() switch
        {
            "ENTER" => "{ENTER}",
            "TAB" => "{TAB}",
            "ESC" or "ESCAPE" => "{ESC}",
            "BACKSPACE" => "{BACKSPACE}",
            "DELETE" => "{DELETE}",
            "CTRL+F" => "^f",
            "CTRL+A" => "^a",
            "CTRL+L" => "^l",
            "CTRL+ENTER" => "^{ENTER}",
            "SHIFT+ENTER" => "+{ENTER}",
            "ARROWDOWN" => "{DOWN}",
            "ARROWUP" => "{UP}",
            _ => null,
        };
        if (sequence is null)
        {
            return new DynamicToolResult(false, "暂不支持这个组合键。 ");
        }

        if (!ActivateWindowHandle(new IntPtr(window.Current.NativeWindowHandle)))
        {
            return new DynamicToolResult(false, "目标窗口暂时无法切换到前台，为避免误按快捷键，本次没有执行。 ");
        }
        Thread.Sleep(90);
        System.Windows.Forms.SendKeys.SendWait(sequence);
        return new DynamicToolResult(true, $"已在“{SafeName(window)}”中按下 {keys.Trim()}。 ");
    }

    private static DynamicToolResult PostVirtualClick(
        AutomationElement window,
        int screenX,
        int screenY,
        int clickCount,
        string successText) =>
        PostVirtualClick(
            new IntPtr(window.Current.NativeWindowHandle),
            screenX,
            screenY,
            clickCount,
            successText);

    private static DynamicToolResult PostVirtualClick(
        IntPtr topHandle,
        int screenX,
        int screenY,
        int clickCount,
        string successText,
        bool requireActivation = true)
    {
        var activated = ActivateWindowHandle(topHandle);
        if (!activated && requireActivation)
        {
            return new DynamicToolResult(false, "目标窗口暂时无法切换到前台，未执行点击。 ");
        }

        var target = ResolveTargetWindowAtPoint(topHandle, screenX, screenY);
        if (target == IntPtr.Zero)
        {
            return new DynamicToolResult(false, "没有在目标窗口内找到可点击区域。 ");
        }

        var clientPoint = new NativePoint(screenX, screenY);
        if (!ScreenToClient(target, ref clientPoint))
        {
            return new DynamicToolResult(false, "无法把屏幕位置换算到目标控件，未执行点击。 ");
        }
        var lParam = MakeLong(clientPoint.X, clientPoint.Y);
        var moved = PostMessage(target, WmMouseMove, IntPtr.Zero, lParam);
        var pressed = PostMessage(target, WmLeftButtonDown, new IntPtr(MkLeftButton), lParam);
        var released = PostMessage(target, WmLeftButtonUp, IntPtr.Zero, lParam);
        if (!moved || !pressed || !released)
        {
            return new DynamicToolResult(false, "目标窗口没有完整接受点击操作。 ");
        }

        if (clickCount == 2)
        {
            Thread.Sleep(Math.Min(110, (int)GetDoubleClickTime() / 3));
            var doublePressed = PostMessage(target, WmLeftButtonDoubleClick, new IntPtr(MkLeftButton), lParam);
            var doubleReleased = PostMessage(target, WmLeftButtonUp, IntPtr.Zero, lParam);
            if (!doublePressed || !doubleReleased)
            {
                return new DynamicToolResult(false, "目标窗口没有完整接受双击操作。 ");
            }
        }
        return new DynamicToolResult(
            true,
            successText,
            PointerX: screenX,
            PointerY: screenY,
            RequiresVerification: true);
    }

    private static IntPtr FindTopLevelWindowAtPoint(int screenX, int screenY)
    {
        var point = new NativePoint(screenX, screenY);
        var hit = WindowFromPoint(point);
        var current = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
        for (var inspected = 0; current != IntPtr.Zero && inspected < 128; inspected++)
        {
            GetWindowThreadProcessId(current, out var processId);
            if (processId != (uint)Environment.ProcessId &&
                IsWindowVisible(current) &&
                GetWindowRect(current, out var rectangle) &&
                screenX >= rectangle.Left && screenX < rectangle.Right &&
                screenY >= rectangle.Top && screenY < rectangle.Bottom)
            {
                return current;
            }

            current = GetWindow(current, GwHwndNext);
        }

        return IntPtr.Zero;
    }

    private static IntPtr ResolveTargetWindowAtPoint(IntPtr topHandle, int screenX, int screenY)
    {
        if (topHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        GetWindowThreadProcessId(topHandle, out var targetProcessId);
        var directHit = WindowFromPoint(new NativePoint(screenX, screenY));
        if (directHit != IntPtr.Zero &&
            directHit != topHandle &&
            IsHandleWithinTarget(directHit, topHandle, targetProcessId))
        {
            return directHit;
        }

        // A topmost presentation overlay can be returned by WindowFromPoint
        // even though it is click-through. Resolve from the intended root
        // instead, descending through visible/enabled/nontransparent children.
        var current = topHandle;
        for (var depth = 0; depth < 16; depth++)
        {
            var clientPoint = new NativePoint(screenX, screenY);
            if (!ScreenToClient(current, ref clientPoint))
            {
                return IntPtr.Zero;
            }

            var child = ChildWindowFromPointEx(
                current,
                clientPoint,
                CwpSkipInvisible | CwpSkipDisabled | CwpSkipTransparent);
            if (child == IntPtr.Zero || child == current)
            {
                break;
            }

            if (!IsHandleWithinTarget(child, topHandle, targetProcessId))
            {
                return IntPtr.Zero;
            }

            current = child;
        }

        return IsHandleWithinTarget(current, topHandle, targetProcessId)
            ? current
            : IntPtr.Zero;
    }

    private static TextDispatchResult DispatchTextToFocusedControl(
        IntPtr topHandle,
        IntPtr nativeFocus,
        string value)
    {
        var clipboardPaste = TryPasteTextPreservingClipboard(topHandle, value);
        if (clipboardPaste.Dispatched)
        {
            return clipboardPaste;
        }

        var unicodeDispatch = SendUnicodeText(value);
        if (unicodeDispatch > 0)
        {
            // A partial SendInput result is deliberately not replayed through a
            // second path: doing so could duplicate the beginning of the text.
            return new TextDispatchResult(true, clipboardPaste.ClipboardRestored);
        }

        var messageTarget = nativeFocus != IntPtr.Zero ? nativeFocus : topHandle;
        return new TextDispatchResult(
            TryPostUnicodeText(messageTarget, value),
            clipboardPaste.ClipboardRestored);
    }

    private static uint SendUnicodeText(string value)
    {
        if (value.Length == 0)
        {
            return 1;
        }

        var inputs = new NativeInput[value.Length * 2];
        var inputIndex = 0;
        foreach (var character in value)
        {
            inputs[inputIndex++] = CreateUnicodeInput(character, keyUp: false);
            inputs[inputIndex++] = CreateUnicodeInput(character, keyUp: true);
        }

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static bool TryPostUnicodeText(IntPtr target, string value)
    {
        if (target == IntPtr.Zero)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!PostMessage(target, WmChar, new IntPtr(character), new IntPtr(1)))
            {
                return false;
            }
        }

        return true;
    }

    private static TextDispatchResult TryPasteTextPreservingClipboard(IntPtr topHandle, string value)
    {
        var replacement = new System.Windows.Forms.DataObject();
        replacement.SetData(System.Windows.Forms.DataFormats.UnicodeText, true, value);
        return TryPasteDataPreservingClipboard(topHandle, replacement, "HuiTian clipboard text input");
    }

    private static TextDispatchResult TryPasteFilesPreservingClipboard(
        IntPtr topHandle,
        IReadOnlyList<string> files)
    {
        var replacement = new System.Windows.Forms.DataObject();
        var collection = new System.Collections.Specialized.StringCollection();
        collection.AddRange(files.ToArray());
        replacement.SetFileDropList(collection);
        return TryPasteDataPreservingClipboard(topHandle, replacement, "HuiTian clipboard file input");
    }

    private static TextDispatchResult TryPasteDataPreservingClipboard(
        IntPtr topHandle,
        System.Windows.Forms.DataObject replacement,
        string threadName)
    {
        var result = new TextDispatchResult(false, true);
        var completed = false;
        var pasteThread = new Thread(() =>
        {
            System.Windows.Forms.IDataObject? previousClipboard = null;
            var capturedClipboard = false;
            var clipboardChanged = false;
            try
            {
                capturedClipboard = TryGetClipboardData(out previousClipboard);
                if (!capturedClipboard)
                {
                    result = new TextDispatchResult(false, true);
                    return;
                }

                if (!TrySetClipboardData(replacement))
                {
                    result = new TextDispatchResult(false, true);
                    return;
                }

                clipboardChanged = true;
                if (!IsForegroundWithinTarget(topHandle))
                {
                    result = new TextDispatchResult(false, true);
                    return;
                }

                var sent = SendControlShortcut(VkV);
                result = new TextDispatchResult(sent > 0, true);
                if (sent > 0)
                {
                    // Give custom/Electron controls time to read every clipboard
                    // format before restoring the user's original data object.
                    Thread.Sleep(180);
                }
            }
            catch
            {
                result = new TextDispatchResult(false, true);
            }
            finally
            {
                if (capturedClipboard && clipboardChanged)
                {
                    var restored = TryRestoreClipboardData(previousClipboard);
                    result = result with { ClipboardRestored = restored };
                }

                completed = true;
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        pasteThread.SetApartmentState(ApartmentState.STA);
        pasteThread.Start();
        if (!pasteThread.Join(TimeSpan.FromSeconds(4)) || !completed)
        {
            // The thread may already have dispatched Ctrl+V. Do not replay by a
            // different path because that could duplicate text.
            return new TextDispatchResult(true, false);
        }

        return result;
    }

    private static bool TryGetClipboardData(out System.Windows.Forms.IDataObject? data)
    {
        data = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var source = System.Windows.Forms.Clipboard.GetDataObject();
                if (source is null)
                {
                    return true;
                }

                // Clipboard.GetDataObject can be a live OLE proxy whose data
                // disappears as soon as the clipboard changes. Materialize all
                // native formats before replacing it so images/files/custom
                // application formats can be put back afterwards.
                var snapshot = new System.Windows.Forms.DataObject();
                foreach (var format in source.GetFormats(autoConvert: false).Distinct(StringComparer.Ordinal))
                {
                    var value = source.GetData(format, autoConvert: false);
                    if (value is null)
                    {
                        continue;
                    }

                    snapshot.SetData(format, autoConvert: false, CloneClipboardValue(value));
                }

                data = snapshot;
                return true;
            }
            catch (ExternalException) when (attempt < 9)
            {
                Thread.Sleep(30);
            }
            catch (Exception) when (attempt < 9)
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }

    private static object CloneClipboardValue(object value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case string[] strings:
                return strings.ToArray();
            case System.Drawing.Image image:
                return image.Clone();
            case MemoryStream memory:
                return new MemoryStream(memory.ToArray(), writable: false);
            case Stream stream:
            {
                var originalPosition = stream.CanSeek ? stream.Position : 0;
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                var clone = new MemoryStream();
                stream.CopyTo(clone);
                clone.Position = 0;
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }

                return clone;
            }
            case ICloneable cloneable:
                return cloneable.Clone();
            default:
                return value;
        }
    }

    private static bool TrySetClipboardData(System.Windows.Forms.IDataObject data)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(data, true, 10, 30);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static bool TryRestoreClipboardData(System.Windows.Forms.IDataObject? data)
    {
        try
        {
            if (data is null)
            {
                System.Windows.Forms.Clipboard.Clear();
            }
            else
            {
                // Re-publish the complete IDataObject rather than only text so
                // images, files and application-specific formats are retained.
                System.Windows.Forms.Clipboard.SetDataObject(data, true, 12, 35);
            }

            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static uint SendControlShortcut(ushort key)
    {
        var inputs = new[]
        {
            CreateVirtualKeyInput(VkControl, keyUp: false),
            CreateVirtualKeyInput(key, keyUp: false),
            CreateVirtualKeyInput(key, keyUp: true),
            CreateVirtualKeyInput(VkControl, keyUp: true),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static bool? VerifyFocusedValue(string expectedText, bool replace)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null ||
                !focused.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern) ||
                rawPattern is not ValuePattern valuePattern)
            {
                return null;
            }

            var actual = valuePattern.Current.Value ?? string.Empty;
            return replace
                ? string.Equals(actual, expectedText, StringComparison.Ordinal)
                : actual.Contains(expectedText, StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryFocusForInput(AutomationElement window, AutomationElement element)
    {
        try
        {
            if (element.Current.IsKeyboardFocusable)
            {
                element.SetFocus();
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Some custom-rendered controls report that they cannot be focused
            // through UIA but accept a native click at their visible center.
        }

        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (!IsUsableBounds(bounds))
            {
                return false;
            }

            var x = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
            var y = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
            PostVirtualClick(window, x, y, 1, string.Empty);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static AutomationElement? GetFocusedElementWithin(AutomationElement window)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            return focused is not null && IsWithinWindow(focused, window)
                ? focused
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static IntPtr GetNativeFocusedHandle(IntPtr topHandle)
    {
        if (topHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var topThread = GetWindowThreadProcessId(topHandle, out var targetProcessId);
        if (TryReadThreadFocus(topThread, out var focused) &&
            IsHandleWithinTarget(focused, topHandle, targetProcessId))
        {
            return focused;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        if (foregroundThread != topThread &&
            TryReadThreadFocus(foregroundThread, out focused) &&
            IsHandleWithinTarget(focused, topHandle, targetProcessId))
        {
            return focused;
        }

        return IntPtr.Zero;
    }

    private static bool TryReadThreadFocus(uint threadId, out IntPtr focused)
    {
        focused = IntPtr.Zero;
        if (threadId == 0)
        {
            return false;
        }

        var info = new GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<GuiThreadInfo>(),
        };
        if (!GetGUIThreadInfo(threadId, ref info))
        {
            return false;
        }

        focused = info.Focus;
        return focused != IntPtr.Zero;
    }

    private static bool IsHandleWithinTarget(IntPtr candidate, IntPtr topHandle, uint targetProcessId)
    {
        if (candidate == IntPtr.Zero || topHandle == IntPtr.Zero)
        {
            return false;
        }

        if (candidate == topHandle || IsChild(topHandle, candidate))
        {
            return true;
        }

        GetWindowThreadProcessId(candidate, out var candidateProcessId);
        return candidateProcessId != 0 &&
               candidateProcessId == targetProcessId &&
               GetAncestor(candidate, GaRoot) == GetAncestor(topHandle, GaRoot);
    }

    private static bool IsForegroundWithinTarget(IntPtr topHandle)
    {
        GetWindowThreadProcessId(topHandle, out var targetProcessId);
        return IsHandleWithinTarget(GetForegroundWindow(), topHandle, targetProcessId);
    }

    private static bool IsWithinWindow(AutomationElement element, AutomationElement window)
    {
        try
        {
            var targetHandle = window.Current.NativeWindowHandle;
            var current = element;
            while (current is not null)
            {
                if (current.Current.NativeWindowHandle == targetHandle)
                {
                    return true;
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private static bool ActivateWindowHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindowAsync(handle, SwRestore);
        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                                 AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread &&
                             AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        Thread.Sleep(80);
        return GetForegroundWindow() == handle;
    }

    private static NativeInput CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                Scan = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
            },
        },
    };

    private static NativeInput CreateVirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    private static IntPtr MakeLong(int low, int high) =>
        new((high << 16) | (low & 0xFFFF));

    internal static AutomationElement? FindWindow(string? requestedTitle)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                var current = AutomationElement.FromHandle(foreground);
                if (!SafeName(current).Contains("回天 AI", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }
        }

        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            Condition.TrueCondition);
        var matches = new List<AutomationElement>();
        var fallbacks = new List<AutomationElement>();
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var name = SafeName(window);
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains("回天 AI", StringComparison.OrdinalIgnoreCase) ||
                !IsUsableTopLevelWindow(window))
            {
                continue;
            }

            fallbacks.Add(window);
            if (!string.IsNullOrWhiteSpace(requestedTitle) &&
                name.Contains(requestedTitle, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(window);
            }
        }

        if (matches.Count > 0)
        {
            // GPU-rendered apps such as WeChat can expose small helper/title
            // windows with the same name. Select the largest visible matching
            // top-level window instead of whichever UI Automation lists first.
            return matches.MaxBy(WindowArea);
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var nativeFallback = FindNativeWindow(requestedTitle);
            if (nativeFallback is not null)
            {
                return nativeFallback;
            }
        }

        return string.IsNullOrWhiteSpace(requestedTitle)
            ? fallbacks.MaxBy(WindowArea)
            : null;
    }

    private static AutomationElement? FindNativeWindow(string requestedTitle)
    {
        var aliases = GetProcessAliases(requestedTitle);
        var candidates = new List<(IntPtr Handle, double Score)>();
        EnumWindows((handle, _) =>
        {
            try
            {
                GetWindowThreadProcessId(handle, out var processId);
                if (processId == 0 || processId == (uint)Environment.ProcessId)
                {
                    return true;
                }

                var visible = IsWindowVisible(handle);
                var iconic = IsIconic(handle);
                if (!visible && !iconic)
                {
                    return true;
                }

                var title = GetNativeWindowText(handle);
                var processName = GetProcessName(processId);
                var titleMatches = !string.IsNullOrWhiteSpace(title) &&
                                   title.Contains(requestedTitle, StringComparison.OrdinalIgnoreCase);
                var processMatches = aliases.Any(alias =>
                    processName.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (!titleMatches && !processMatches)
                {
                    return true;
                }

                GetWindowRect(handle, out var rectangle);
                var width = Math.Max(0, rectangle.Right - rectangle.Left);
                var height = Math.Max(0, rectangle.Bottom - rectangle.Top);
                var area = (double)width * height;
                var score = titleMatches ? 1000d : 300d;
                if (title.Equals(requestedTitle, StringComparison.OrdinalIgnoreCase)) score += 250;
                if (visible) score += 140;
                if (!iconic) score += 90;
                if (GetWindow(handle, GwOwner) == IntPtr.Zero) score += 80;
                score += Math.Min(300, Math.Log10(Math.Max(1, area)) * 45);
                candidates.Add((handle, score));
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
            }

            return true;
        }, IntPtr.Zero);

        foreach (var candidate in candidates.OrderByDescending(value => value.Score))
        {
            try
            {
                return AutomationElement.FromHandle(candidate.Handle);
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetProcessAliases(string requestedTitle)
    {
        var value = requestedTitle.Trim();
        if (value.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("wechat", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("weixin", StringComparison.OrdinalIgnoreCase))
        {
            return ["Weixin", "WeChat", "WeChatAppEx"];
        }

        if (value.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("腾讯QQ", StringComparison.OrdinalIgnoreCase))
        {
            return ["QQ", "QQApp", "QQProtect"];
        }

        if (value.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("浏览器", StringComparison.OrdinalIgnoreCase))
        {
            return ["msedge"];
        }

        if (value.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("谷歌", StringComparison.OrdinalIgnoreCase))
        {
            return ["chrome"];
        }

        if (value.Contains("Word", StringComparison.OrdinalIgnoreCase)) return ["WINWORD"];
        if (value.Contains("Excel", StringComparison.OrdinalIgnoreCase)) return ["EXCEL"];
        if (value.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PPT", StringComparison.OrdinalIgnoreCase)) return ["POWERPNT"];
        return [Path.GetFileNameWithoutExtension(value)];
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static string GetNativeWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0
            ? builder.ToString().Trim()
            : string.Empty;
    }

    private static bool IsUsableTopLevelWindow(AutomationElement element)
    {
        try
        {
            var handle = new IntPtr(element.Current.NativeWindowHandle);
            var bounds = element.Current.BoundingRectangle;
            return handle != IntPtr.Zero && IsWindowVisible(handle) &&
                   !bounds.IsEmpty && bounds.Width >= 80 && bounds.Height >= 40;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static double WindowArea(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
        }
        catch (ElementNotAvailableException)
        {
            return 0;
        }
    }

    private static AutomationElement? FindNamedElement(
        AutomationElement window,
        string requestedName)
    {
        if (IsGenericInputRequest(requestedName))
        {
            return FindBestEditableElement(window);
        }

        var exact = new PropertyCondition(
            AutomationElement.NameProperty,
            requestedName,
            PropertyConditionFlags.IgnoreCase);
        var found = window.FindFirst(TreeScope.Descendants, exact);
        if (found is not null)
        {
            return found;
        }

        var exactAutomationId = new PropertyCondition(
            AutomationElement.AutomationIdProperty,
            requestedName,
            PropertyConditionFlags.IgnoreCase);
        found = window.FindFirst(TreeScope.Descendants, exactAutomationId);
        if (found is not null)
        {
            return found;
        }

        var nodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var index = 0; index < nodes.Count; index++)
        {
            var name = SafeName(nodes[index]);
            var automationId = SafeAutomationId(nodes[index]);
            if (name.Contains(requestedName, StringComparison.OrdinalIgnoreCase) ||
                automationId.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return nodes[index];
            }
        }

        return null;
    }

    private static bool IsGenericInputRequest(string requestedName)
    {
        var normalized = requestedName.Trim();
        return GenericInputNames.Any(name =>
            normalized.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static AutomationElement? FindBestEditableElement(AutomationElement window)
    {
        var windowBounds = window.Current.BoundingRectangle;
        var nodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement? best = null;
        var bestScore = double.MinValue;
        for (var index = 0; index < nodes.Count; index++)
        {
            var candidate = nodes[index];
            if (!TryScoreEditableCandidate(candidate, windowBounds, out var score))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static bool TryScoreEditableCandidate(
        AutomationElement element,
        System.Windows.Rect windowBounds,
        out double score)
    {
        score = 0;
        try
        {
            var current = element.Current;
            var controlType = current.ControlType;
            if (controlType != ControlType.Edit && controlType != ControlType.Document)
            {
                return false;
            }

            var bounds = current.BoundingRectangle;
            if (!current.IsEnabled || current.IsOffscreen || !IsUsableBounds(bounds))
            {
                return false;
            }

            var supportsWritableValue = false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue) &&
                rawValue is ValuePattern valuePattern)
            {
                supportsWritableValue = !valuePattern.Current.IsReadOnly;
            }

            if (!current.IsKeyboardFocusable && !supportsWritableValue)
            {
                return false;
            }

            score = controlType == ControlType.Edit ? 160 : 140;
            if (current.IsKeyboardFocusable)
            {
                score += 35;
            }

            if (supportsWritableValue)
            {
                score += 30;
            }

            var semanticLabel = $"{SafeName(element)} {SafeAutomationId(element)}";
            if (semanticLabel.Contains("输入", StringComparison.OrdinalIgnoreCase) ||
                semanticLabel.Contains("消息", StringComparison.OrdinalIgnoreCase) ||
                semanticLabel.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                semanticLabel.Contains("editor", StringComparison.OrdinalIgnoreCase) ||
                semanticLabel.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                semanticLabel.Contains("message", StringComparison.OrdinalIgnoreCase))
            {
                score += 34;
            }

            if (windowBounds.Height > 0)
            {
                var bottomRatio = Math.Clamp(
                    (bounds.Bottom - windowBounds.Top) / windowBounds.Height,
                    0,
                    1);
                score += bottomRatio * 80;
                if (bounds.Height > windowBounds.Height * 0.55)
                {
                    score -= 70;
                }
            }

            if (windowBounds.Width > 0)
            {
                score += Math.Clamp(bounds.Width / windowBounds.Width, 0, 1) * 18;
            }

            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsInspectableUnnamedInput(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            return (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document) &&
                   current.IsKeyboardFocusable &&
                   current.IsEnabled &&
                   !current.IsOffscreen &&
                   IsUsableBounds(current.BoundingRectangle);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsUsableBounds(System.Windows.Rect bounds) =>
        !bounds.IsEmpty &&
        !double.IsNaN(bounds.Left) &&
        !double.IsNaN(bounds.Top) &&
        !double.IsInfinity(bounds.Left) &&
        !double.IsInfinity(bounds.Top) &&
        bounds.Width > 1 &&
        bounds.Height > 1;

    private static string SafeElementLabel(AutomationElement element)
    {
        var name = SafeName(element);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var automationId = SafeAutomationId(element);
        return string.IsNullOrWhiteSpace(automationId)
            ? "输入区域"
            : automationId;
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name?.Trim() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static string SafeAutomationId(AutomationElement element)
    {
        try
        {
            return element.Current.AutomationId?.Trim() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static string SafeControlTypeName(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType?.LocalizedControlType ?? "控件";
        }
        catch (ElementNotAvailableException)
        {
            return "控件";
        }
    }

    private static bool SafeIsEnabled(AutomationElement element)
    {
        try
        {
            return element.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attached);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rectangle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;

        // INPUT's native union is sized by MOUSEINPUT (32 bytes on x64), not
        // KEYBDINPUT. Without this member Marshal.SizeOf<INPUT>() is too small
        // and user32 rejects every SendInput keyboard operation.
        [FieldOffset(0)]
        public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private sealed record ScreenCaptureMap(
        IntPtr WindowHandle,
        bool IsDesktop,
        int ImageWidth,
        int ImageHeight,
        System.Drawing.Rectangle PhysicalBounds,
        DateTimeOffset CapturedAt,
        string ContentHash);

    private sealed record ChatContactCandidate(
        AutomationElement Element,
        string Label,
        double Score,
        System.Windows.Rect Bounds);

    private sealed record ChatTargetVerification(bool Verified, string Detail);

    private sealed record GpuTargetPreparation(
        bool Verified,
        string ResultCode,
        string Detail,
        string? VerifiedTitle);

    private sealed record OpenChatTarget(
        AutomationElement? Window,
        IntPtr TopHandle,
        string StrategyPlatform,
        AutomationElement? Search,
        AutomationElementCollection? Snapshot,
        string ContactLabel,
        string Strategy,
        string VerificationDetail,
        DynamicToolResult? Failure)
    {
        public static OpenChatTarget Failed(DynamicToolResult failure) =>
            new(
                Window: null,
                TopHandle: IntPtr.Zero,
                StrategyPlatform: string.Empty,
                Search: null,
                Snapshot: null,
                ContactLabel: string.Empty,
                Strategy: string.Empty,
                VerificationDetail: string.Empty,
                Failure: failure);
    }

    private readonly record struct TextDispatchResult(bool Dispatched, bool ClipboardRestored);
}
