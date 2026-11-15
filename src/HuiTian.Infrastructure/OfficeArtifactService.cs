using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HuiTian.Infrastructure;

/// <summary>
/// Deterministic Office artifact operations kept outside the language layer.
/// New files are written to D:, modifications create a reversible copy, and a
/// tool result is successful only after the resulting artifact is reopened.
/// </summary>
internal static class OfficeArtifactService
{
    public static DynamicToolResult CreateSpreadsheet(JsonElement arguments)
    {
        var title = ReadString(arguments, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return new DynamicToolResult(false, "表格需要一个明确标题。", ResultCode: "missing_title");
        }

        var headers = ReadStringArray(arguments, "headers").ToList();
        if (headers.Count == 0)
        {
            headers.AddRange(ReadStringArray(arguments, "columns"));
        }

        var rows = new List<IReadOnlyList<object?>>();
        if (arguments.TryGetProperty("rows", out var rowValues) &&
            rowValues.ValueKind == JsonValueKind.Array)
        {
            var sourceRows = rowValues.EnumerateArray().Take(10_000).ToArray();
            if (headers.Count == 0 && sourceRows.FirstOrDefault().ValueKind == JsonValueKind.Object)
            {
                headers.AddRange(sourceRows[0].EnumerateObject()
                    .Take(256)
                    .Select(property => property.Name));
            }

            foreach (var row in sourceRows)
            {
                if (row.ValueKind == JsonValueKind.Array)
                {
                    rows.Add(row.EnumerateArray().Take(256).Select(ToCellValue).ToArray());
                }
                else if (row.ValueKind == JsonValueKind.Object)
                {
                    rows.Add(headers.Select(header =>
                        row.TryGetProperty(header, out var value) ? ToCellValue(value) : null).ToArray());
                }
            }
        }

        if (headers.Count == 0 && rows.Count == 0)
        {
            return new DynamicToolResult(
                false,
                "表格至少需要列名或一行数据。",
                ResultCode: "missing_spreadsheet_data");
        }

        var outputDirectory = @"D:\HuiTian\UserContent\表格";
        Directory.CreateDirectory(outputDirectory);
        var requestedName = ReadString(arguments, "fileName");
        var safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(requestedName)
            ? title
            : Path.GetFileNameWithoutExtension(requestedName));
        var outputPath = UniquePath(outputDirectory, safeName, ".xlsx");
        var sheetName = ReadString(arguments, "sheetName") ?? "数据";
        try
        {
            var result = OpenXmlSpreadsheetWriter.Create(
                outputPath,
                title,
                sheetName,
                headers,
                rows);
            return result.Success
                ? new DynamicToolResult(
                    true,
                    $"可编辑表格已创建并重新打开核验：{outputPath}\n" +
                    $"数据规模：{rows.Count} 行，{Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count))} 列。",
                    ResultCode: "artifact_verified",
                    ArtifactPaths: [outputPath])
                : new DynamicToolResult(
                    false,
                    result.Detail,
                    ResultCode: "verification_failed",
                    ArtifactPaths: File.Exists(outputPath) ? [outputPath] : []);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new DynamicToolResult(
                false,
                $"表格没有创建成功：{exception.Message}",
                ResultCode: "spreadsheet_creation_failed");
        }
    }

    public static DynamicToolResult ModifyFile(JsonElement arguments)
    {
        var sourceText = ReadString(arguments, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(sourceText) || !Path.IsPathFullyQualified(sourceText))
        {
            return new DynamicToolResult(false, "请提供要修改文件的完整路径。", ResultCode: "missing_path");
        }

        var source = Path.GetFullPath(sourceText);
        if (!File.Exists(source))
        {
            return new DynamicToolResult(false, "要修改的原文件不存在。", ResultCode: "source_missing");
        }

        if (IsProtectedProductOrSystemPath(source))
        {
            return new DynamicToolResult(false, "为保护系统和回天核心，不能修改这个位置的文件。", ResultCode: "protected_path");
        }

        var replacements = ReadReplacements(arguments);
        var newContent = ReadString(arguments, "newContent");
        var cellUpdates = ReadCellUpdates(arguments);
        OpenXmlPresentationEditor.Style? presentationStyle;
        try { presentationStyle = OpenXmlPresentationEditor.ReadStyle(arguments); }
        catch (ArgumentException exception) { return new DynamicToolResult(false, exception.Message, ResultCode: "invalid_arguments"); }
        if (replacements.Count == 0 && newContent is null && cellUpdates.Count == 0 && presentationStyle is null)
        {
            return new DynamicToolResult(
                false,
                "没有提供可执行的文字替换、新正文、单元格更新或 presentationStyle 格式变更。",
                ResultCode: "missing_modification");
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (presentationStyle is not null && extension != ".pptx")
            return new DynamicToolResult(false, "presentationStyle 仅用于 PPTX 演示文稿。", ResultCode: "invalid_arguments");
        var supported = extension is ".txt" or ".md" or ".csv" or ".tsv" or ".json" or
            ".xml" or ".html" or ".css" or ".js" or ".ts" or ".cs" or ".py" or
            ".ps1" or ".docx" or ".xlsx" or ".pptx";
        if (!supported)
        {
            return new DynamicToolResult(
                false,
                "当前可安全修改文字文件、DOCX、XLSX 和 PPTX。",
                ResultCode: "unsupported_format");
        }

        var outputDirectory = @"D:\HuiTian\UserContent\已修改";
        Directory.CreateDirectory(outputDirectory);
        var requestedName = ReadString(arguments, "outputFileName");
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileNameWithoutExtension(source) + "-已修改"
            : Path.GetFileNameWithoutExtension(requestedName);
        var output = UniquePath(outputDirectory, MakeSafeFileName(baseName), extension);
        var beforeHash = ComputeSha256(source);

        try
        {
            bool changed;
            string? editDetail = null;
            if (IsPlainTextExtension(extension))
            {
                changed = ModifyTextFile(source, output, newContent, replacements);
            }
            else if (extension == ".docx")
            {
                changed = ModifyWordDocument(source, output, newContent, replacements);
            }
            else if (extension == ".xlsx")
            {
                changed = ModifySpreadsheet(source, output, replacements, cellUpdates);
            }
            else
            {
                var edit = OpenXmlPresentationEditor.Edit(source, output, replacements, presentationStyle);
                changed = edit.Changed;
                editDetail = $"共 {edit.SlideCount} 页，处理 {edit.StyledSlideCount} 页，更新 {edit.EditedPartCount} 个文件部件。{edit.Detail}";
            }

            if (!changed || !File.Exists(output))
            {
                TryDeleteCreatedOutput(output);
                return new DynamicToolResult(
                    false,
                    "没有在原文件中找到要修改的内容，未生成冒充完成的副本。",
                    ResultCode: "no_change");
            }

            var afterHash = ComputeSha256(output);
            if (!string.Equals(beforeHash, ComputeSha256(source), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase) ||
                !VerifyArtifactPackage(output, extension))
            {
                return new DynamicToolResult(
                    false,
                    $"修改副本已生成，但前后差异或文件结构没有通过核验：{output}",
                    ResultCode: "verification_failed",
                    ArtifactPaths: [output]);
            }

            return new DynamicToolResult(
                true,
                $"文件已修改为新副本并重新打开核验：{output}\n原件保持不变：{source}\n" +
                $"修改前指纹：{beforeHash[..12]}；修改后指纹：{afterHash[..12]}。{editDetail}",
                ResultCode: "modified_file_verified",
                ArtifactPaths: [output]);
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(
                false,
                $"文件修改没有完成：{exception.Message}",
                ResultCode: "modification_failed",
                ArtifactPaths: File.Exists(output) ? [output] : []);
        }
    }

    public static bool VerifyPresentation(string path, int minimumSlides)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 1_000) return false;
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("ppt/presentation.xml") is null) return false;
            return archive.Entries.Count(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) >= minimumSlides;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool ModifyTextFile(
        string source,
        string output,
        string? newContent,
        IReadOnlyList<(string Find, string Replace)> replacements)
    {
        var original = File.ReadAllText(source, Encoding.UTF8);
        var revised = newContent ?? ApplyReplacements(original, replacements);
        if (string.Equals(original, revised, StringComparison.Ordinal)) return false;
        File.WriteAllText(output, revised, new UTF8Encoding(false));
        return string.Equals(File.ReadAllText(output, Encoding.UTF8), revised, StringComparison.Ordinal);
    }

    private static bool ModifyWordDocument(
        string source,
        string output,
        string? newContent,
        IReadOnlyList<(string Find, string Replace)> replacements)
    {
        if (newContent is not null)
        {
            var title = Path.GetFileNameWithoutExtension(source);
            return OpenXmlDocumentWriter.Create(output, title, newContent).Success;
        }

        var type = Type.GetTypeFromProgID("Word.Application")
                   ?? throw new InvalidOperationException("这台电脑没有可用于修改文档的 Word。");
        File.Copy(source, output, overwrite: false);
        dynamic? app = null;
        dynamic? document = null;
        var changed = false;
        try
        {
            app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.DisplayAlerts = 0;
            document = app.Documents.Open(output, ReadOnly: false, Visible: false);
            foreach (var replacement in replacements)
            {
                dynamic find = document.Content.Find;
                find.ClearFormatting();
                find.Replacement.ClearFormatting();
                bool replaced = find.Execute(
                    FindText: replacement.Find,
                    ReplaceWith: replacement.Replace,
                    Replace: 2,
                    Forward: true,
                    Wrap: 1);
                changed |= replaced;
            }

            if (changed) document.Save();
            return changed;
        }
        finally
        {
            try { document?.Close(false); } catch { }
            try { app?.Quit(false); } catch { }
        }
    }

    private static bool ModifySpreadsheet(
        string source,
        string output,
        IReadOnlyList<(string Find, string Replace)> replacements,
        IReadOnlyList<SpreadsheetCellUpdate> updates)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("这台电脑没有可用于修改表格的 Excel。");
        File.Copy(source, output, overwrite: false);
        dynamic? app = null;
        dynamic? workbook = null;
        var changed = false;
        try
        {
            app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.DisplayAlerts = false;
            workbook = app.Workbooks.Open(output, ReadOnly: false);
            foreach (var update in updates)
            {
                dynamic sheet = string.IsNullOrWhiteSpace(update.Sheet)
                    ? workbook.Worksheets.Item(1)
                    : workbook.Worksheets.Item(update.Sheet);
                dynamic cell = sheet.Range[update.Cell];
                var before = Convert.ToString(cell.Formula);
                cell.Formula = update.Value;
                changed |= !string.Equals(before, Convert.ToString(cell.Formula), StringComparison.Ordinal);
            }

            foreach (dynamic sheet in workbook.Worksheets)
            {
                foreach (var replacement in replacements)
                {
                    bool replaced = sheet.UsedRange.Replace(
                        replacement.Find,
                        replacement.Replace,
                        2,
                        1,
                        false,
                        false,
                        false,
                        false);
                    changed |= replaced;
                }
            }

            if (changed) workbook.Save();
            return changed;
        }
        finally
        {
            try { workbook?.Close(false); } catch { }
            try { app?.Quit(); } catch { }
        }
    }

    private static bool ModifyPresentation(
        string source,
        string output,
        IReadOnlyList<(string Find, string Replace)> replacements)
    {
        if (replacements.Count == 0)
        {
            throw new InvalidOperationException("修改演示文稿需要提供文字替换项。");
        }

        var type = Type.GetTypeFromProgID("PowerPoint.Application")
                   ?? throw new InvalidOperationException("这台电脑没有可用于修改演示文稿的 PowerPoint。");
        File.Copy(source, output, overwrite: false);
        dynamic? app = null;
        dynamic? presentation = null;
        var changed = false;
        try
        {
            app = Activator.CreateInstance(type)!;
            presentation = app.Presentations.Open(output, ReadOnly: false, WithWindow: false);
            for (var slideIndex = 1; slideIndex <= (int)presentation.Slides.Count; slideIndex++)
            {
                dynamic slide = presentation.Slides.Item(slideIndex);
                for (var shapeIndex = 1; shapeIndex <= (int)slide.Shapes.Count; shapeIndex++)
                {
                    dynamic shape = slide.Shapes.Item(shapeIndex);
                    if (shape.HasTextFrame != -1 || shape.TextFrame.HasText != -1) continue;
                    string text = Convert.ToString(shape.TextFrame.TextRange.Text) ?? string.Empty;
                    var revised = ApplyReplacements(text, replacements);
                    if (string.Equals(text, revised, StringComparison.Ordinal)) continue;
                    shape.TextFrame.TextRange.Text = revised;
                    changed = true;
                }
            }

            if (changed) presentation.Save();
            return changed;
        }
        finally
        {
            try { presentation?.Close(); } catch { }
            try { app?.Quit(); } catch { }
        }
    }

    private static string ApplyReplacements(
        string source,
        IReadOnlyList<(string Find, string Replace)> replacements)
    {
        var result = source;
        foreach (var replacement in replacements)
        {
            result = result.Replace(replacement.Find, replacement.Replace, StringComparison.Ordinal);
        }

        return result;
    }

    private static IReadOnlyList<(string Find, string Replace)> ReadReplacements(JsonElement arguments)
    {
        var result = new List<(string Find, string Replace)>();
        if (!arguments.TryGetProperty("replacements", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var value in values.EnumerateArray().Take(100))
        {
            var find = ReadString(value, "find");
            var replace = ReadString(value, "replace") ?? string.Empty;
            if (!string.IsNullOrEmpty(find)) result.Add((find, replace));
        }

        return result;
    }

    private static IReadOnlyList<SpreadsheetCellUpdate> ReadCellUpdates(JsonElement arguments)
    {
        var result = new List<SpreadsheetCellUpdate>();
        if (!arguments.TryGetProperty("cellUpdates", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var value in values.EnumerateArray().Take(2_000))
        {
            var cell = ReadString(value, "cell")?.Trim();
            if (string.IsNullOrWhiteSpace(cell) ||
                !value.TryGetProperty("value", out var content)) continue;
            result.Add(new SpreadsheetCellUpdate(
                ReadString(value, "sheet"),
                cell,
                ToCellValue(content)));
        }

        return result;
    }

    private static object? ToCellValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };

    private static bool VerifyArtifactPackage(string path, string extension)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        if (extension is not (".docx" or ".xlsx" or ".pptx")) return true;
        using var archive = ZipFile.OpenRead(path);
        return extension switch
        {
            ".docx" => archive.GetEntry("word/document.xml") is { Length: > 0 },
            ".xlsx" => archive.GetEntry("xl/workbook.xml") is { Length: > 0 } &&
                       archive.Entries.Any(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)),
            ".pptx" => archive.GetEntry("ppt/presentation.xml") is { Length: > 0 } &&
                       archive.Entries.Any(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static bool IsPlainTextExtension(string extension) => extension is
        ".txt" or ".md" or ".csv" or ".tsv" or ".json" or ".xml" or
        ".html" or ".css" or ".js" or ".ts" or ".cs" or ".py" or ".ps1";

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string UniquePath(string directory, string baseName, string extension)
    {
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(directory, $"{baseName}-{suffix}{extension}");
        for (var index = 2; File.Exists(candidate); index++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{suffix}-{index}{extension}");
        }

        return candidate;
    }

    private static string MakeSafeFileName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "回天产物" : value.Trim();
        var safe = string.Join("_", source.Split(
            Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "回天产物" : safe;
    }

    private static bool IsProtectedProductOrSystemPath(string path)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"D:\HuiTian\Project", @"D:\HuiTian\Runtime", @"D:\HuiTian\Models",
        };
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            .Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                         fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteCreatedOutput(string path)
    {
        try
        {
            var allowedRoot = Path.GetFullPath(@"D:\HuiTian\UserContent\已修改")
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
        }
    }

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string[] ReadStringArray(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .Take(256)
                .ToArray()
            : [];

    private sealed record SpreadsheetCellUpdate(string? Sheet, string Cell, object? Value);
}
