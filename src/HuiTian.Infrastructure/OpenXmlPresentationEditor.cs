using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HuiTian.Infrastructure;

/// <summary>Edits package parts, not an Office process or screen coordinates.</summary>
internal static class OpenXmlPresentationEditor
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly string[] FillNames = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    internal sealed record Style(
        int[]? Slides, string? BackgroundColor, string? FontColor,
        string? FontFamily, double? FontSize, bool? Bold)
    {
        public bool HasFontStyle => FontColor is not null || FontFamily is not null || FontSize is not null || Bold is not null;
    }

    internal sealed record EditResult(bool Changed, int SlideCount, int StyledSlideCount, int EditedPartCount, string Detail);

    internal static DynamicToolResult ReadText(string path, int startPage = 1, int maxPages = 5, int maxChars = 6000)
    {
        maxChars = Math.Clamp(maxChars, 1000, 20000);
        using var archive = ZipFile.OpenRead(path);
        var slides = ReadOrderedSlides(archive);
        if (startPage < 1 || startPage > slides.Count)
            return new DynamicToolResult(false, $"共 {slides.Count} 页，startPage 超出范围。", ResultCode: "invalid_arguments");
        var builder = new System.Text.StringBuilder($"文件：{path}\n共 {slides.Count} 页。整体格式修改可直接使用 modify_file.presentationStyle，无需读完整正文。\n");
        var lastPage = Math.Min(slides.Count, startPage + Math.Clamp(maxPages, 1, 20) - 1);
        for (var page = startPage; page <= lastPage; page++)
        {
            builder.AppendLine($"第 {page} 页");
            builder.AppendLine(string.Join("\n", Texts(Load(archive, slides[page - 1]))));
            if (builder.Length > maxChars)
            {
                // Tell the reader explicitly when a page was only partly read.
                builder.Length = maxChars;
                builder.AppendLine($"\n第 {page} 页内容截断，可增加 maxChars 重读该页。后续请指定 startPage。全文件未读完。");
                return new DynamicToolResult(true, builder.ToString(), ResultCode: "document_excerpt");
            }
        }
        if (lastPage < slides.Count) builder.AppendLine($"后续从 startPage={lastPage + 1} 读取；全文件未读完。");
        return new DynamicToolResult(true, builder.ToString(), ResultCode: "document_excerpt");
    }

    internal static Style? ReadStyle(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("presentationStyle", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("presentationStyle 必须是格式对象。");
        int[]? slides = null;
        if (value.TryGetProperty("slides", out var selected))
        {
            if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() == 0)
                throw new ArgumentException("slides 必须包含从 1 开始的页码；全部页面请省略 slides。");
            slides = selected.EnumerateArray().Select(item =>
                item.TryGetInt32(out var number) && number > 0 ? number : throw new ArgumentException("幻灯片页码必须是正整数。"))
                .Distinct().ToArray();
        }
        string? ReadText(string name)
        {
            if (!value.TryGetProperty(name, out var property)) return null;
            if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
                throw new ArgumentException($"{name} 必须是非空字符串。");
            return property.GetString()!.Trim();
        }
        string? ReadColor(string name)
        {
            var text = ReadText(name)?.TrimStart('#');
            if (text is null) return null;
            if (text.Length != 6 || text.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException($"{name} 必须是六位 RGB 颜色，例如 FFFFFF。");
            return text.ToUpperInvariant();
        }
        double? size = null;
        if (value.TryGetProperty("fontSize", out var fontSize))
        {
            if (!fontSize.TryGetDouble(out var points) || !double.IsFinite(points) || points is < 1 or > 400)
                throw new ArgumentException("fontSize 必须在 1 到 400 磅之间。");
            size = points;
        }
        bool? bold = null;
        if (value.TryGetProperty("bold", out var boldValue))
        {
            if (boldValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("bold 必须是布尔值。");
            bold = boldValue.GetBoolean();
        }
        var result = new Style(slides, ReadColor("backgroundColor"), ReadColor("fontColor"), ReadText("fontFamily"), size, bold);
        if (result.BackgroundColor is null && !result.HasFontStyle)
            throw new ArgumentException("presentationStyle 没有任何要修改的格式。");
        return result;
    }

    internal static EditResult Edit(string source, string output,
        IReadOnlyList<(string Find, string Replace)> replacements, Style? style)
    {
        using var original = ZipFile.OpenRead(source);
        if (original.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该演示文稿带有数字签名，修改会破坏签名，需要先由用户处理签名副本。");
        var orderedSlides = ReadOrderedSlides(original);
        if (style?.Slides?.Any(number => number > orderedSlides.Count) == true)
            throw new ArgumentException($"指定页码超出范围，这份演示文稿共有 {orderedSlides.Count} 页。");
        var selected = (style?.Slides ?? Enumerable.Range(1, orderedSlides.Count).ToArray())
            .Select(number => orderedSlides[number - 1]).ToHashSet(StringComparer.Ordinal);
        var allSlides = selected.Count == orderedSlides.Count;
        var revisions = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var expectedTexts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var editableFontParts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in orderedSlides)
        {
            var document = Load(original, path);
            var before = document.ToString(SaveOptions.DisableFormatting);
            if (replacements.Count > 0) ReplaceText(document, replacements);
            if (style is not null && selected.Contains(path))
            {
                if (style.BackgroundColor is not null) SetBackground(document, style.BackgroundColor);
                if (style.HasFontStyle)
                {
                    ApplyFont(document, style);
                    editableFontParts.Add(path);
                }
            }
            expectedTexts[path] = Texts(document);
            if (before != document.ToString(SaveOptions.DisableFormatting)) revisions[path] = document;
        }

        // Global formatting must include inherited master/layout text and embedded
        // chart/SmartArt text, not just the obvious top-level slide text boxes.
        if (style?.HasFontStyle == true && allSlides)
        {
            foreach (var entry in original.Entries.Where(entry => IsInheritedTextPart(entry.FullName)))
            {
                var document = Load(original, entry.FullName);
                var before = document.ToString(SaveOptions.DisableFormatting);
                ApplyFont(document, style);
                expectedTexts[entry.FullName] = Texts(document);
                editableFontParts.Add(entry.FullName);
                if (before != document.ToString(SaveOptions.DisableFormatting)) revisions[entry.FullName] = document;
            }
        }

        if (revisions.Count == 0)
            return new EditResult(false, orderedSlides.Count, selected.Count, 0, "请求的格式或文字已经相同。");

        // Copy all untouched parts byte-for-byte after decompression. Images,
        // relationships, notes, shape positions, animations and content are kept.
        using (var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var edited = new ZipArchive(outputStream, ZipArchiveMode.Create))
        {
            foreach (var entry in original.Entries)
            {
                var target = edited.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                target.LastWriteTime = entry.LastWriteTime;
                target.ExternalAttributes = entry.ExternalAttributes;
                using var stream = target.Open();
                if (revisions.TryGetValue(entry.FullName, out var xml)) xml.Save(stream, SaveOptions.DisableFormatting);
                else
                {
                    using var input = entry.Open();
                    input.CopyTo(stream);
                }
            }
        }

        // Reopen the actual saved copy. Compare all preserved parts and all text,
        // then inspect the requested formatting, rather than trusting a save call.
        using var verified = ZipFile.OpenRead(output);
        if (!ReadOrderedSlides(verified).SequenceEqual(orderedSlides))
            throw new InvalidDataException("修改后幻灯片顺序核验失败。");
        if (verified.Entries.Count != original.Entries.Count)
            throw new InvalidDataException("修改后文件部件数量核验失败。");
        foreach (var entry in original.Entries)
        {
            var target = verified.GetEntry(entry.FullName) ?? throw new InvalidDataException("修改后缺少原始文件部件。");
            if (!revisions.ContainsKey(entry.FullName))
            {
                using var a = entry.Open();
                using var b = target.Open();
                if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)))
                    throw new InvalidDataException($"原有部件被意外修改：{entry.FullName}");
            }
        }
        foreach (var (path, text) in expectedTexts)
            if (!Texts(Load(verified, path)).SequenceEqual(text)) throw new InvalidDataException("修改后文字内容核验失败。");
        if (style is not null)
        {
            foreach (var path in selected)
            {
                var document = Load(verified, path);
                if (style.BackgroundColor is not null &&
                    (string?)document.Root?.Element(P + "cSld")?.Element(P + "bg")?.Element(P + "bgPr")?
                        .Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val") != style.BackgroundColor)
                    throw new InvalidDataException("幻灯片背景颜色核验失败。");
            }
            foreach (var path in editableFontParts) VerifyFont(Load(verified, path), style);
        }
        var note = style?.Slides is not null && !allSlides && style.HasFontStyle
            ? "分页格式只改变所选页的文字，共用母版及图表文字保持原样。"
            : "已保留页序、文字、图片、版式和原始文件。";
        if (style?.BackgroundColor is not null)
            note += "背景设置针对幻灯片背景层，不会删除覆盖其上的图片或装饰形状；图片内嵌文字不作猜测修改。";
        return new EditResult(true, orderedSlides.Count, selected.Count, revisions.Count, note);
    }

    private static void SetBackground(XDocument document, string color)
    {
        var common = document.Root?.Element(P + "cSld") ?? throw new InvalidDataException("幻灯片缺少 cSld。");
        var background = new XElement(P + "bg", new XElement(P + "bgPr",
            new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color))),
            new XElement(A + "effectLst")));
        var previous = common.Element(P + "bg");
        if (previous is null) common.AddFirst(background);
        else previous.ReplaceWith(background);
    }

    private static void ApplyFont(XDocument document, Style style)
    {
        foreach (var run in document.Descendants().Where(element => element.Name == A + "r" || element.Name == A + "fld").ToArray())
        {
            if (!run.Elements(A + "t").Any()) continue;
            var properties = run.Element(A + "rPr");
            if (properties is null) { properties = new XElement(A + "rPr"); run.AddFirst(properties); }
            SetFont(properties, style);
        }
        foreach (var paragraph in document.Descendants(A + "p").ToArray())
        {
            var properties = paragraph.Element(A + "pPr");
            if (properties is null) { properties = new XElement(A + "pPr"); paragraph.AddFirst(properties); }
            var defaults = properties.Element(A + "defRPr");
            if (defaults is null)
            {
                defaults = new XElement(A + "defRPr");
                var extensions = properties.Element(A + "extLst");
                if (extensions is null) properties.Add(defaults); else extensions.AddBeforeSelf(defaults);
            }
            SetFont(defaults, style);
            var end = paragraph.Element(A + "endParaRPr");
            if (end is not null) SetFont(end, style);
        }
        foreach (var properties in document.Descendants().Where(element =>
                     element.Name == A + "defRPr" || element.Name == A + "endParaRPr" || element.Name == A + "rPr").ToArray())
            SetFont(properties, style);
        foreach (var reference in document.Descendants(A + "fontRef"))
        {
            if (style.FontColor is null) continue;
            reference.Elements().Remove();
            reference.Add(new XElement(A + "srgbClr", new XAttribute("val", style.FontColor)));
        }
    }

    private static void SetFont(XElement properties, Style style)
    {
        if (style.FontColor is not null)
        {
            properties.Elements().Where(element => element.Name.Namespace == A && FillNames.Contains(element.Name.LocalName)).Remove();
            var fill = new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", style.FontColor)));
            var line = properties.Element(A + "ln");
            if (line is null) properties.AddFirst(fill); else line.AddAfterSelf(fill);
        }
        if (style.FontSize is not null) properties.SetAttributeValue("sz", (int)Math.Round(style.FontSize.Value * 100));
        if (style.Bold is not null) properties.SetAttributeValue("b", style.Bold.Value ? "1" : "0");
        if (style.FontFamily is not null)
        {
            foreach (var name in new[] { "latin", "ea", "cs" })
            {
                var face = properties.Element(A + name);
                if (face is null)
                {
                    face = new XElement(A + name);
                    var following = properties.Elements().FirstOrDefault(element => element.Name.Namespace == A &&
                        (name == "latin" && element.Name.LocalName is "ea" or "cs" ||
                         name == "ea" && element.Name.LocalName == "cs" ||
                         element.Name.LocalName is "sym" or "hlinkClick" or "hlinkMouseOver" or "rtl" or "extLst"));
                    if (following is null) properties.Add(face); else following.AddBeforeSelf(face);
                }
                face.SetAttributeValue("typeface", style.FontFamily);
            }
        }
    }

    private static void VerifyFont(XDocument document, Style style)
    {
        foreach (var properties in document.Descendants().Where(element =>
                     element.Name == A + "rPr" || element.Name == A + "defRPr" || element.Name == A + "endParaRPr"))
        {
            if (style.FontColor is not null && (string?)properties.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val") != style.FontColor ||
                style.FontSize is not null && (int?)properties.Attribute("sz") != (int)Math.Round(style.FontSize.Value * 100) ||
                style.Bold is not null && (string?)properties.Attribute("b") != (style.Bold.Value ? "1" : "0") ||
                style.FontFamily is not null && new[] { "latin", "ea", "cs" }.Any(name => (string?)properties.Element(A + name)?.Attribute("typeface") != style.FontFamily))
                throw new InvalidDataException("演示文稿字体格式核验失败。");
        }
    }

    private static void ReplaceText(XDocument document, IReadOnlyList<(string Find, string Replace)> replacements)
    {
        foreach (var paragraph in document.Descendants(A + "p"))
        {
            foreach (var (find, replace) in replacements)
            {
                var nodes = paragraph.Descendants(A + "t").ToArray();
                var text = string.Concat(nodes.Select(node => node.Value));
                var matches = new List<int>();
                for (var index = 0; index <= text.Length - find.Length;)
                {
                    var match = text.IndexOf(find, index, StringComparison.Ordinal);
                    if (match < 0) break;
                    matches.Add(match); index = match + find.Length;
                }
                // Reverse offsets retain existing runs, hyperlinks and formatting.
                foreach (var match in matches.AsEnumerable().Reverse())
                {
                    var offset = 0;
                    var inserted = false;
                    foreach (var node in nodes)
                    {
                        var value = node.Value;
                        var end = offset + value.Length;
                        if (offset < match + find.Length && end > match)
                        {
                            var startInNode = Math.Max(0, match - offset);
                            var endInNode = Math.Min(value.Length, match + find.Length - offset);
                            node.Value = value[..startInNode] + (inserted ? "" : replace) + value[endInNode..];
                            inserted = true;
                        }
                        offset = end;
                    }
                }
            }
        }
    }

    private static bool IsInheritedTextPart(string path) => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        (path.StartsWith("ppt/slideMasters/", StringComparison.Ordinal) || path.StartsWith("ppt/slideLayouts/", StringComparison.Ordinal) ||
         path.StartsWith("ppt/charts/", StringComparison.Ordinal) || path.StartsWith("ppt/diagrams/", StringComparison.Ordinal));

    private static string[] Texts(XDocument document) => document.Descendants(A + "t").Select(text => text.Value).ToArray();

    private static List<string> ReadOrderedSlides(ZipArchive archive)
    {
        var document = Load(archive, "ppt/presentation.xml");
        var relationships = Load(archive, "ppt/_rels/presentation.xml.rels");
        var targets = relationships.Root!.Elements(Rel + "Relationship")
            .Where(item => (string?)item.Attribute("TargetMode") != "External")
            .ToDictionary(item => (string)item.Attribute("Id")!, item => (string)item.Attribute("Target")!, StringComparer.Ordinal);
        var slides = new List<string>();
        foreach (var slide in document.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            var id = (string?)slide.Attribute(R + "id");
            if (id is null || !targets.TryGetValue(id, out var target)) throw new InvalidDataException("幻灯片关系缺失。");
            var uri = new Uri(new Uri("https://package.local/ppt/presentation.xml"), target);
            var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (uri.Host != "package.local" || !path.StartsWith("ppt/slides/", StringComparison.Ordinal) || archive.GetEntry(path) is null)
                throw new InvalidDataException("幻灯片关系无效。");
            slides.Add(path);
        }
        if (slides.Count == 0 || slides.Distinct(StringComparer.Ordinal).Count() != slides.Count)
            throw new InvalidDataException("演示文稿没有可编辑的有效页面。");
        return slides;
    }

    private static XDocument Load(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"演示文稿缺少 {path}。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}
