namespace HuiTian.Infrastructure;

/// <summary>Only for side-effect-free observations, never click/type/save operations.</summary>
internal sealed class BoundedUiRead
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private readonly string _unavailableText;
    private Task<DynamicToolResult>? _pending;

    internal BoundedUiRead(TimeSpan? timeout = null, string? unavailableText = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(1500);
        _unavailableText = unavailableText ??
            "控件读取未及时响应；本次只读，没有点击或输入。请改用截图观察或直接文件/应用能力，不要重复请求完整控件树，也不需要用户接管。";
    }

    internal async Task<DynamicToolResult> ReadAsync(Func<DynamicToolResult> read, CancellationToken token)
    {
        Task<DynamicToolResult> pending;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) return Unavailable();
            pending = _pending = Task.Run(read, CancellationToken.None);
        }
        try { return await pending.WaitAsync(_timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { return Unavailable(); }
    }

    private DynamicToolResult Unavailable() => new(false,
        _unavailableText,
        ResultCode: "observation_unavailable");
}
