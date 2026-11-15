using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.IO;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

public abstract class NotifyModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public sealed class MessageItem : NotifyModel
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public string RoleLabel => Role == "user" ? "你" : "回天";
    public string RoleMark => Role == "user" ? "你" : "回";
    public string TimeLabel => Created.ToLocalTime().ToString("HH:mm");
    public string RoleColor => Role == "user" ? "#E9EFF3" : "#D9F1F0";
}

public sealed class TimelineItem
{
    public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
    public string Label { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class Conversation : NotifyModel
{
    private string _title = "新的任务";
    private bool _isRunning;
    private string _runStatus = "等待开始";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set { _title = value; Changed(); } }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    public ObservableCollection<MessageItem> Messages { get; set; } = [];
    public ObservableCollection<TimelineItem> Timeline { get; set; } = [];
    public ObservableCollection<Artifact> Artifacts { get; set; } = [];
    public string Draft { get; set; } = "";
    public ObservableCollection<string> Attachments { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRunning { get => _isRunning; set { _isRunning = value; Changed(); Changed(nameof(StatusHint)); } }
    public string RunStatus { get => _runStatus; set { _runStatus = value; Changed(); Changed(nameof(StatusHint)); } }
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusHint => IsRunning ? RunStatus : Messages.Count == 0 ? "还没有消息" : Updated.ToLocalTime().ToString("MM.dd  HH:mm");
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Messages.Count == 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMessages => Messages.Count > 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasArtifacts => Artifacts.Count > 0;
    public void Refresh() { Changed(nameof(IsEmpty)); Changed(nameof(HasMessages)); Changed(nameof(HasArtifacts)); Changed(nameof(StatusHint)); }
}

public sealed class ConversationStore
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public ConversationStore(BusinessPaths paths)
    {
        _directory = Path.Combine(paths.Data, "Conversations");
        Directory.CreateDirectory(_directory);
    }
    public IReadOnlyList<Conversation> Load()
    {
        var loaded = new List<Conversation>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var conversation = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Options);
                if (conversation is not null && Guid.TryParseExact(conversation.Id, "N", out _))
                {
                    if (conversation.Messages is null || conversation.Timeline is null || conversation.Artifacts is null ||
                        conversation.Attachments is null || conversation.Messages.Any(m => m is null ||
                            m.Role is not ("user" or "assistant") || m.Content is null)) continue;
                    if (conversation.RunStatus is "执行中" or "规划中" or "正在准备模型" or "等待执行" or "等待确认" or "正在停止")
                        conversation.RunStatus = "上次运行已中断";
                    loaded.Add(conversation);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* Preserve unreadable files in place. */ }
        }
        return loaded.OrderByDescending(c => c.Updated).ToList();
    }
    public void Save(Conversation conversation)
    {
        if (!Guid.TryParseExact(conversation.Id, "N", out _)) throw new InvalidDataException("无效的对话编号。");
        var destination = Path.Combine(_directory, conversation.Id + ".json");
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(conversation, Options));
        File.Move(temporary, destination, true);
    }
}

public sealed record CapabilityDisplay(string Name, string Description, string Risk)
{
    public static CapabilityDisplay FromTool(ToolDefinition tool)
    {
        var (name, description) = tool.Name switch
        {
            "capabilities" => ("能力查询", "查看当前已注册的能力与限制；暂未连接微信，不支持自动读取或发送聊天。"),
            "list_files" => ("浏览本机文件", "列出你指定的文件夹内容。"),
            "search_files" => ("查找本机文件", "按文件名在指定文件夹中查找，限制搜索范围与数量。"),
            "read_file" => ("读取任务材料", "读取文本、Markdown、CSV、TSV、Word、Excel、PowerPoint 的有限文本。暂不支持 PDF 解析。"),
            "create_document" => ("生成办公文件", "创建 Word、Excel、PowerPoint 或文本文件；保存到交付目录并检查结果。"),
            "change_presentation_style" => ("调整演示样式", "在演示文件副本中修改背景和字体。"),
            "deduplicate_worksheet" => ("整理重复数据", "按指定列在 Excel 副本中去重；复杂公式、合并单元格和表格结构暂不处理。"),
            "list_apps" => ("查找已注册应用", "查询 Windows 已注册的桌面应用；部分商店应用可能无法发现。"),
            "open_app" => ("打开桌面应用", "打开上一步查找到的已注册应用。"),
            "desktop_list_windows" => ("发现桌面窗口", "读取当前可见窗口，返回本次观察的窗口编号。"),
            "desktop_activate_window" => ("切换目标窗口", "切到已经发现的窗口，并检查它确实成为前台窗口。"),
            "desktop_read_window" => ("读取软件控件", "读取软件公开的按钮、列表和输入框；自绘或受保护界面可能无法读取。"),
            "desktop_invoke" => ("操作软件控件", "根据真实窗口和控件确认后执行；可能产生不可逆影响，提交动作后仍需检查业务结果。"),
            "desktop_fill" => ("填写软件输入框", "只填写刚观察到的非密码输入框，并回读文字；不把填入当成发送成功。"),
            "browser_open" => ("打开公开网页", "使用独立浏览器访问公开网页；不访问本机与内网地址。"),
            "browser_read" => ("读取网页内容", "读取当前浏览器页面的可见文字与可操作元素；登录、验证码需人工处理。"),
            "browser_click" => ("操作网页元素", "点击已经观察到的网页元素。可能产生外部变更，每次执行前需要确认。"),
            "browser_fill" => ("填写网页文本", "在已观察到的输入框填写文字，每次执行前需要确认。不填写密码或上传文件。"),
            "browser_upload" => ("添加网页附件", "确认后向已观察到的文件框附加指定文件并检查名称及大小；不代表服务器已收到或已经发送。"),
            _ => (tool.Name, tool.Description)
        };
        return new(name, description, tool.Risk switch { RiskLevel.ReadOnly => "读取", RiskLevel.Reversible => "本地操作", RiskLevel.External => "需要你的确认", RiskLevel.Critical => "高风险确认", _ => "未知" });
    }
}

public sealed class WorkspaceViewModel : NotifyModel
{
    private Conversation? _selected;
    private string _runtimeTitle = "检查本地环境";
    private string _runtimeDetail = "正在读取模型与服务状态";
    private string _hardwareText = "正在读取硬件信息";
    private string _modelText = "尚未检查";
    public ObservableCollection<Conversation> Conversations { get; } = [];
    public Conversation? Selected { get => _selected; set { _selected = value; Changed(); } }
    public string RuntimeTitle { get => _runtimeTitle; set { _runtimeTitle = value; Changed(); } }
    public string RuntimeDetail { get => _runtimeDetail; set { _runtimeDetail = value; Changed(); } }
    public string HardwareText { get => _hardwareText; set { _hardwareText = value; Changed(); } }
    public string ModelText { get => _modelText; set { _modelText = value; Changed(); } }
}
