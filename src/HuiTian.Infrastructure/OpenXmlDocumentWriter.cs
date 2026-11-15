using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HuiTian.Infrastructure;

/// <summary>
/// Creates a standards-based, printable DOCX without requiring Word to be
/// installed.  The model supplies document content; this class owns only the
/// deterministic file format, layout, and post-write verification.
/// </summary>
internal static class OpenXmlDocumentWriter
{
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static DocumentWriteResult Create(
        string path,
        string title,
        string content,
        bool landscape = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes());
            WriteEntry(archive, "_rels/.rels", PackageRelationships());
            WriteEntry(archive, "docProps/core.xml", CoreProperties(title));
            WriteEntry(archive, "docProps/app.xml", AppProperties());
            WriteEntry(archive, "word/styles.xml", Styles());
            WriteEntry(archive, "word/_rels/document.xml.rels", DocumentRelationships());
            WriteEntry(archive, "word/document.xml", BuildDocument(title, content, landscape));
        }

        return Verify(path, title, content);
    }

    public static DocumentWriteResult Verify(string path, string title, string content)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 1_000)
        {
            return new DocumentWriteResult(false, "生成的文档文件不存在或内容为空。", path);
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var documentEntry = archive.GetEntry("word/document.xml");
            if (documentEntry is null)
            {
                return new DocumentWriteResult(false, "文档缺少正文结构，无法作为有效 Word 文件交付。", path);
            }

            using var stream = documentEntry.Open();
            var document = XDocument.Load(stream);
            var extracted = string.Join("\n", document.Descendants(W + "t")
                .Select(node => node.Value));
            var expected = NormalizeExpectedContent(content);
            var actual = Normalize(extracted);
            if (!actual.Contains(Normalize(title), StringComparison.Ordinal) ||
                expected.Length > 0 && !actual.Contains(expected, StringComparison.Ordinal))
            {
                return new DocumentWriteResult(false, "文档已写入，但重新打开核验时正文不完整。", path);
            }

            return new DocumentWriteResult(
                true,
                $"文档已创建、重新打开并核验：{path}",
                path);
        }
        catch (InvalidDataException exception)
        {
            return new DocumentWriteResult(false, $"文档结构核验失败：{exception.Message}", path);
        }
    }

    private static XDocument BuildDocument(
        string title,
        string content,
        bool landscape)
    {
        var body = new XElement(W + "body");
        body.Add(Paragraph(title, "Title", center: true));

        foreach (var line in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var value = line.TrimEnd();
            if (value is "[[PAGE_BREAK]]" or "[分页]")
            {
                body.Add(new XElement(W + "p",
                    new XElement(W + "r", new XElement(W + "br",
                        new XAttribute(W + "type", "page")))));
                continue;
            }

            if (value.StartsWith("### ", StringComparison.Ordinal))
            {
                body.Add(Paragraph(value[4..], "Heading2"));
            }
            else if (value.StartsWith("## ", StringComparison.Ordinal))
            {
                body.Add(Paragraph(value[3..], "Heading1"));
            }
            else if (value.StartsWith("# ", StringComparison.Ordinal))
            {
                body.Add(Paragraph(value[2..], "Heading1"));
            }
            else if (value == "---")
            {
                body.Add(RuleParagraph());
            }
            else
            {
                body.Add(Paragraph(value));
            }
        }

        var pageWidth = landscape ? "16838" : "11906";
        var pageHeight = landscape ? "11906" : "16838";
        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz",
                new XAttribute(W + "w", pageWidth),
                new XAttribute(W + "h", pageHeight),
                landscape ? new XAttribute(W + "orient", "landscape") : null),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", "1134"),
                new XAttribute(W + "right", "1134"),
                new XAttribute(W + "bottom", "1134"),
                new XAttribute(W + "left", "1134"),
                new XAttribute(W + "header", "708"),
                new XAttribute(W + "footer", "708"),
                new XAttribute(W + "gutter", "0"))));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W),
                new XAttribute(XNamespace.Xmlns + "r", R),
                body));
    }

    private static XElement Paragraph(
        string text,
        string? style = null,
        bool center = false)
    {
        var properties = new XElement(W + "pPr");
        if (!string.IsNullOrWhiteSpace(style))
        {
            properties.Add(new XElement(W + "pStyle", new XAttribute(W + "val", style)));
        }

        if (center)
        {
            properties.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));
        }

        properties.Add(new XElement(W + "spacing",
            new XAttribute(W + "line", "360"),
            new XAttribute(W + "lineRule", "auto"),
            new XAttribute(W + "after", string.IsNullOrEmpty(text) ? "240" : "100")));
        return new XElement(W + "p",
            properties,
            new XElement(W + "r",
                new XElement(W + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    text)));
    }

    private static XElement RuleParagraph() => new(W + "p",
        new XElement(W + "pPr",
            new XElement(W + "pBdr",
                new XElement(W + "bottom",
                    new XAttribute(W + "val", "single"),
                    new XAttribute(W + "sz", "8"),
                    new XAttribute(W + "space", "1"),
                    new XAttribute(W + "color", "B8B2A6")))));

    private static XDocument Styles() => new(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", W),
            Style("Normal", "正文", "宋体", "22", isDefault: true),
            Style("Title", "标题", "Microsoft YaHei", "36", bold: true),
            Style("Heading1", "一级标题", "Microsoft YaHei", "28", bold: true),
            Style("Heading2", "二级标题", "Microsoft YaHei", "24", bold: true)));

    private static XElement Style(
        string id,
        string name,
        string font,
        string size,
        bool bold = false,
        bool isDefault = false) => new(W + "style",
        new XAttribute(W + "type", "paragraph"),
        new XAttribute(W + "styleId", id),
        isDefault ? new XAttribute(W + "default", "1") : null,
        new XElement(W + "name", new XAttribute(W + "val", name)),
        new XElement(W + "qFormat"),
        new XElement(W + "rPr",
            new XElement(W + "rFonts",
                new XAttribute(W + "ascii", font),
                new XAttribute(W + "hAnsi", font),
                new XAttribute(W + "eastAsia", font)),
            bold ? new XElement(W + "b") : null,
            new XElement(W + "sz", new XAttribute(W + "val", size)),
            new XElement(W + "szCs", new XAttribute(W + "val", size))));

    private static XDocument ContentTypes() => XDocument.Parse("""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
          <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
        </Types>
        """);

    private static XDocument PackageRelationships() => XDocument.Parse("""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
        </Relationships>
        """);

    private static XDocument DocumentRelationships() => XDocument.Parse("""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """);

    private static XDocument CoreProperties(string title)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(cp + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", cp),
                new XAttribute(XNamespace.Xmlns + "dc", dc),
                new XAttribute(XNamespace.Xmlns + "dcterms", dcterms),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement(dc + "title", title),
                new XElement(dc + "creator", "回天 AI"),
                new XElement(cp + "lastModifiedBy", "回天 AI"),
                new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now),
                new XElement(dcterms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now)));
    }

    private static XDocument AppProperties() => XDocument.Parse("""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
          <Application>回天 AI</Application>
          <DocSecurity>0</DocSecurity>
          <ScaleCrop>false</ScaleCrop>
          <Company></Company>
          <AppVersion>1.0</AppVersion>
        </Properties>
        """);

    private static void WriteEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string Normalize(string value) => string.Concat(
        (value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));

    private static string NormalizeExpectedContent(string content)
    {
        var rendered = content.ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => line.Trim() is not ("[[PAGE_BREAK]]" or "[分页]" or "---"))
            .Select(line =>
            {
                var value = line.TrimEnd();
                if (value.StartsWith("### ", StringComparison.Ordinal)) return value[4..];
                if (value.StartsWith("## ", StringComparison.Ordinal)) return value[3..];
                if (value.StartsWith("# ", StringComparison.Ordinal)) return value[2..];
                return value;
            });
        return Normalize(string.Join("\n", rendered));
    }
}

internal sealed record DocumentWriteResult(bool Success, string Detail, string Path);
