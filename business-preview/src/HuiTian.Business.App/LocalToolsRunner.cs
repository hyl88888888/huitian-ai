using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuiTian.Business.Adapters;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

public enum LocalOperation { PresentationStyle, WorksheetDeduplication, ExtractText }
public sealed record LocalOperationRequest(LocalOperation Operation, string Source, string FileName,
    string BackgroundColor = "FFFFFF", string FontFamily = "Microsoft YaHei", string FontColor = "172D3D",
    string SheetName = "Sheet1", string KeyColumns = "A", int MaxCharacters = 64000);
public sealed record LocalOperationResult(string Summary, IReadOnlyList<Artifact> Artifacts, bool Partial = false, bool CancellationRequested = false);

/// <summary>Explicit form fields map to bounded local adapters. No language model or intent routing is involved.</summary>
public sealed class LocalToolsRunner : IAsyncDisposable
{
    private readonly IToolRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public LocalToolsRunner(BusinessPaths paths, IToolRegistry? registry = null) => _registry = registry ?? new DesktopToolRegistry(paths);

    public async Task<LocalOperationResult> ExecuteAsync(LocalOperationRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有一个本地操作正在执行，请等待完成或请求取消。");
        try
        {
            Validate(request);
            // Office package operations are synchronous. Keep the UI responsive and never abandon an in-flight write.
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Operation == LocalOperation.ExtractText)
                {
                    progress?.Report("正在读取文档文字…");
                    var read = await _registry.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = request.Source, maxCharacters = request.MaxCharacters }), cancellationToken);
                    EnsureSuccess(read);
                    using var evidence = JsonDocument.Parse(read.Evidence);
                    if (!evidence.RootElement.TryGetProperty("text", out var content) || content.ValueKind != JsonValueKind.String ||
                        !evidence.RootElement.TryGetProperty("truncated", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("读取结果缺少文本或完整性标记，未创建提取文件。");
                    var text = content.GetString()!;
                    var partial = flag.GetBoolean();
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(partial ? $"仅提取前 {text.Length:N0} 个字符；正在保存部分文本…" : "正在保存文字提取文件…");
                    if (partial) text = $"【部分提取】源文档超过本次 {request.MaxCharacters:N0} 字符的读取上限；下文不代表完整文档。\n\n" + text;
                    var created = await _registry.ExecuteAsync("create_document", JsonSerializer.SerializeToElement(new
                    {
                        kind = "txt", fileName = request.FileName, title = "", content = text, rows = Array.Empty<string[]>(), slides = Array.Empty<object>()
                    }), cancellationToken);
                    EnsureSuccess(created);
                    return VerifiedResult(created, partial ? "已保存部分提取的文字。源文档超过读取上限，TXT 内已标明，不代表完整文档。" : "已提取当前工具可读取的正文文字并保存为 TXT。图片中的文字与版式不包含在提取结果中。", partial, cancellationToken);
                }
                var name = request.Operation == LocalOperation.PresentationStyle ? "change_presentation_style" : "deduplicate_worksheet";
                var arguments = request.Operation == LocalOperation.PresentationStyle
                    ? JsonSerializer.SerializeToElement(new { path = request.Source, fileName = request.FileName, backgroundColor = NormalizeColor(request.BackgroundColor), fontFamily = request.FontFamily.Trim(), fontColor = NormalizeColor(request.FontColor) })
                    : JsonSerializer.SerializeToElement(new { path = request.Source, fileName = request.FileName, sheetName = request.SheetName.Trim(), keyColumns = ParseColumns(request.KeyColumns) });
                progress?.Report("正在创建新副本并核验文件结构…");
                var result = await _registry.ExecuteAsync(name, arguments, cancellationToken);
                EnsureSuccess(result);
                return VerifiedResult(result, result.Summary, false, cancellationToken);
            }, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }
    private static LocalOperationResult VerifiedResult(ToolResult result, string summary, bool partial, CancellationToken cancellationToken)
    {
        var artifacts = result.Artifacts ?? [];
        if (artifacts.Count == 0 || artifacts.Any(a => !File.Exists(a.Path))) throw new IOException("操作没有返回可核验的输出文件，未标记为完成。");
        // A cancellation request may arrive during a synchronous write. Report the real saved artifact instead of hiding it.
        return new(summary, artifacts, partial, cancellationToken.IsCancellationRequested);
    }
    private static void EnsureSuccess(ToolResult result)
    {
        if (!result.Success || result.NeedsHuman) throw new InvalidOperationException(result.Summary);
        if (string.IsNullOrWhiteSpace(result.Evidence)) throw new InvalidDataException("工具没有提供核验结果。");
    }
    private static void Validate(LocalOperationRequest request)
    {
        if (!Enum.IsDefined(request.Operation)) throw new ArgumentException("请选择一个本地操作。");
        if (!Path.IsPathFullyQualified(request.Source) || !File.Exists(request.Source)) throw new ArgumentException("请先选择一个存在的本机文件。");
        var sourceExtension = Path.GetExtension(request.Source).ToLowerInvariant();
        var expected = request.Operation switch { LocalOperation.PresentationStyle => ".pptx", LocalOperation.WorksheetDeduplication => ".xlsx", _ => ".txt" };
        if (request.Operation == LocalOperation.ExtractText ? sourceExtension is not (".docx" or ".xlsx" or ".pptx" or ".txt") : sourceExtension != expected)
            throw new ArgumentException("输入格式与所选操作不匹配，请重新选择文件。");
        if (string.IsNullOrWhiteSpace(request.FileName) || Path.GetFileName(request.FileName) != request.FileName || !request.FileName.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"输出只填写文件名，扩展名必须为 {expected}。");
        if (request.Operation == LocalOperation.PresentationStyle)
        {
            if (!Regex.IsMatch(NormalizeColor(request.BackgroundColor), "^[A-Fa-f0-9]{6}$") || !Regex.IsMatch(NormalizeColor(request.FontColor), "^[A-Fa-f0-9]{6}$"))
                throw new ArgumentException("颜色请输入六位十六进制色值，例如 FFFFFF 或 #172D3D。");
            if (string.IsNullOrWhiteSpace(request.FontFamily)) throw new ArgumentException("请填写字体名称。");
        }
        if (request.Operation == LocalOperation.WorksheetDeduplication)
        {
            if (string.IsNullOrWhiteSpace(request.SheetName)) throw new ArgumentException("请填写工作表名称，例如 Sheet1。");
            var columns = ParseColumns(request.KeyColumns);
            if (columns.Length == 0 || columns.Any(c => !Regex.IsMatch(c, "^[A-Z]{1,3}$"))) throw new ArgumentException("去重列填写列字母，多列用逗号分隔，例如 A,C。");
        }
        if (request.Operation == LocalOperation.ExtractText && request.MaxCharacters is < 1 or > 64000) throw new ArgumentException("文字提取上限须为 1 至 64,000 个字符。");
    }
    private static string NormalizeColor(string color) => color.Trim().TrimStart('#');
    private static string[] ParseColumns(string columns) => columns.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToUpperInvariant()).Distinct().ToArray();
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try { if (_disposed) return; _disposed = true; if (_registry is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        finally { _gate.Release(); }
    }
}
