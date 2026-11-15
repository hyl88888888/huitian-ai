using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HuiTian.Infrastructure;

/// <summary>
/// Creates a real XLSX workbook without requiring Excel or a paid SDK.  The
/// caller supplies already-structured values; this writer owns the package,
/// basic readable styling, formulas and deterministic reopen verification.
/// </summary>
internal static class OpenXmlSpreadsheetWriter
{
    private static readonly XNamespace S =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static SpreadsheetWriteResult Create(
        string path,
        string title,
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var safeSheetName = NormalizeSheetName(sheetName);
        var columnCount = Math.Max(
            1,
            Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count)));
        if (columnCount > 16_384)
        {
            return new SpreadsheetWriteResult(false, "表格列数超过 Excel 可用范围。", path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes());
            WriteEntry(archive, "_rels/.rels", PackageRelationships());
            WriteEntry(archive, "docProps/core.xml", CoreProperties(title));
            WriteEntry(archive, "docProps/app.xml", AppProperties(safeSheetName));
            WriteEntry(archive, "xl/workbook.xml", Workbook(safeSheetName));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
            WriteEntry(archive, "xl/styles.xml", Styles());
            WriteEntry(
                archive,
                "xl/worksheets/sheet1.xml",
                Worksheet(title, headers, rows, columnCount));
        }

        return Verify(path, title, safeSheetName, headers, rows.Count);
    }

    public static SpreadsheetWriteResult Verify(
        string path,
        string title,
        string sheetName,
        IReadOnlyList<string> headers,
        int expectedDataRows)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 1_000)
        {
            return new SpreadsheetWriteResult(false, "生成的表格不存在或内容为空。", path);
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var workbookEntry = archive.GetEntry("xl/workbook.xml");
            var worksheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
            var stylesEntry = archive.GetEntry("xl/styles.xml");
            if (workbookEntry is null || worksheetEntry is null || stylesEntry is null)
            {
                return new SpreadsheetWriteResult(false, "表格缺少工作簿、工作表或样式结构。", path);
            }

            using var workbookStream = workbookEntry.Open();
            var workbook = XDocument.Load(workbookStream);
            var actualSheet = workbook.Descendants(S + "sheet")
                .Select(node => (string?)node.Attribute("name"))
                .FirstOrDefault();
            if (!string.Equals(actualSheet, sheetName, StringComparison.Ordinal))
            {
                return new SpreadsheetWriteResult(false, "重新打开核验时工作表名称不一致。", path);
            }

            using var worksheetStream = worksheetEntry.Open();
            var worksheet = XDocument.Load(worksheetStream);
            var text = string.Join("\n", worksheet.Descendants(S + "t").Select(node => node.Value));
            if (!Normalize(text).Contains(Normalize(title), StringComparison.Ordinal))
            {
                return new SpreadsheetWriteResult(false, "重新打开核验时标题不完整。", path);
            }

            foreach (var header in headers.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!Normalize(text).Contains(Normalize(header), StringComparison.Ordinal))
                {
                    return new SpreadsheetWriteResult(false, $"重新打开核验时缺少列名：{header}", path);
                }
            }

            var expectedRows = 1 + (headers.Count > 0 ? 1 : 0) + expectedDataRows;
            var actualRows = worksheet.Descendants(S + "row").Count();
            if (actualRows < expectedRows)
            {
                return new SpreadsheetWriteResult(false, "重新打开核验时数据行不完整。", path);
            }

            return new SpreadsheetWriteResult(
                true,
                $"表格已创建、重新打开并核验：{path}",
                path);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return new SpreadsheetWriteResult(false, $"表格结构核验失败：{exception.Message}", path);
        }
    }

    private static XDocument Worksheet(
        string title,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int columnCount)
    {
        var sheetData = new XElement(S + "sheetData");
        sheetData.Add(new XElement(
            S + "row",
            new XAttribute("r", 1),
            new XAttribute("ht", 30),
            new XAttribute("customHeight", 1),
            CreateCell("A1", title, 1)));

        var dataStartRow = 2;
        if (headers.Count > 0)
        {
            var headerRow = new XElement(S + "row", new XAttribute("r", 2));
            for (var index = 0; index < headers.Count; index++)
            {
                headerRow.Add(CreateCell(CellReference(index, 2), headers[index], 2));
            }

            sheetData.Add(headerRow);
            dataStartRow = 3;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var excelRow = dataStartRow + rowIndex;
            var row = new XElement(S + "row", new XAttribute("r", excelRow));
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                row.Add(CreateCell(
                    CellReference(columnIndex, excelRow),
                    rows[rowIndex][columnIndex],
                    0));
            }

            sheetData.Add(row);
        }

        var widths = Enumerable.Range(0, columnCount)
            .Select(index => CalculateColumnWidth(index, headers, rows))
            .ToArray();
        var columns = new XElement(S + "cols");
        for (var index = 0; index < widths.Length; index++)
        {
            columns.Add(new XElement(
                S + "col",
                new XAttribute("min", index + 1),
                new XAttribute("max", index + 1),
                new XAttribute("width", widths[index].ToString("0.##", CultureInfo.InvariantCulture)),
                new XAttribute("customWidth", 1)));
        }

        var lastColumn = ColumnName(columnCount - 1);
        var lastRow = Math.Max(1, dataStartRow + rows.Count - 1);
        var worksheet = new XElement(
            S + "worksheet",
            new XAttribute(XNamespace.Xmlns + "r", R),
            new XElement(S + "dimension", new XAttribute("ref", $"A1:{lastColumn}{lastRow}")),
            new XElement(
                S + "sheetViews",
                new XElement(
                    S + "sheetView",
                    new XAttribute("workbookViewId", 0),
                    new XElement(
                        S + "pane",
                        new XAttribute("ySplit", headers.Count > 0 ? 2 : 1),
                        new XAttribute("topLeftCell", $"A{dataStartRow}"),
                        new XAttribute("activePane", "bottomLeft"),
                        new XAttribute("state", "frozen")))),
            new XElement(S + "sheetFormatPr", new XAttribute("defaultRowHeight", 20)),
            columns,
            sheetData,
            new XElement(S + "mergeCells",
                new XAttribute("count", 1),
                new XElement(S + "mergeCell", new XAttribute("ref", $"A1:{lastColumn}1"))));

        if (headers.Count > 0)
        {
            worksheet.Add(new XElement(
                S + "autoFilter",
                new XAttribute("ref", $"A2:{lastColumn}{Math.Max(2, lastRow)}")));
        }

        worksheet.Add(new XElement(
            S + "pageMargins",
            new XAttribute("left", "0.35"),
            new XAttribute("right", "0.35"),
            new XAttribute("top", "0.6"),
            new XAttribute("bottom", "0.6"),
            new XAttribute("header", "0.2"),
            new XAttribute("footer", "0.2")));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), worksheet);
    }

    private static XElement CreateCell(string reference, object? value, int style)
    {
        var cell = new XElement(
            S + "c",
            new XAttribute("r", reference),
            new XAttribute("s", style));
        if (value is null)
        {
            return cell;
        }

        if (value is bool boolean)
        {
            cell.Add(new XAttribute("t", "b"), new XElement(S + "v", boolean ? "1" : "0"));
            return cell;
        }

        if (IsNumber(value, out var number))
        {
            cell.Add(new XElement(S + "v", number));
            return cell;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Length > 1 && text[0] == '=')
        {
            cell.Add(new XElement(S + "f", text[1..]));
            return cell;
        }

        cell.Add(
            new XAttribute("t", "inlineStr"),
            new XElement(
                S + "is",
                new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        return cell;
    }

    private static bool IsNumber(object value, out string number)
    {
        number = value switch
        {
            byte item => item.ToString(CultureInfo.InvariantCulture),
            sbyte item => item.ToString(CultureInfo.InvariantCulture),
            short item => item.ToString(CultureInfo.InvariantCulture),
            ushort item => item.ToString(CultureInfo.InvariantCulture),
            int item => item.ToString(CultureInfo.InvariantCulture),
            uint item => item.ToString(CultureInfo.InvariantCulture),
            long item => item.ToString(CultureInfo.InvariantCulture),
            ulong item => item.ToString(CultureInfo.InvariantCulture),
            float item when float.IsFinite(item) => item.ToString("R", CultureInfo.InvariantCulture),
            double item when double.IsFinite(item) => item.ToString("R", CultureInfo.InvariantCulture),
            decimal item => item.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
        return number.Length > 0;
    }

    private static double CalculateColumnWidth(
        int columnIndex,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var maximum = columnIndex < headers.Count ? VisualLength(headers[columnIndex]) : 0;
        foreach (var row in rows.Take(200))
        {
            if (columnIndex < row.Count)
            {
                maximum = Math.Max(maximum, VisualLength(Convert.ToString(
                    row[columnIndex],
                    CultureInfo.InvariantCulture) ?? string.Empty));
            }
        }

        return Math.Clamp(maximum + 3, 10, 42);
    }

    private static int VisualLength(string value) => value.Sum(character => character > 0xFF ? 2 : 1);

    private static string CellReference(int zeroBasedColumn, int row) =>
        ColumnName(zeroBasedColumn) + row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnName(int zeroBasedColumn)
    {
        var value = zeroBasedColumn + 1;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }

        return builder.ToString();
    }

    private static string NormalizeSheetName(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "数据" : value.Trim();
        foreach (var character in new[] { '[', ']', ':', '*', '?', '/', '\\' })
        {
            result = result.Replace(character, ' ');
        }

        result = result.Trim(' ', '\'');
        if (result.Length == 0) result = "数据";
        return result.Length <= 31 ? result : result[..31];
    }

    private static XDocument ContentTypes() => XDocument.Parse(
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
          <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
        </Types>
        """);

    private static XDocument PackageRelationships() => XDocument.Parse(
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
        </Relationships>
        """);

    private static XDocument Workbook(string sheetName) => new(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(
            S + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", R),
            new XElement(S + "bookViews", new XElement(S + "workbookView", new XAttribute("activeTab", 0))),
            new XElement(
                S + "sheets",
                new XElement(
                    S + "sheet",
                    new XAttribute("name", sheetName),
                    new XAttribute("sheetId", 1),
                    new XAttribute(R + "id", "rId1"))),
            new XElement(S + "calcPr", new XAttribute("calcId", 191029), new XAttribute("fullCalcOnLoad", 1))));

    private static XDocument WorkbookRelationships() => XDocument.Parse(
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """);

    private static XDocument Styles() => XDocument.Parse(
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="3">
            <font><sz val="11"/><name val="等线"/><family val="2"/></font>
            <font><b/><sz val="16"/><color rgb="FFFFFFFF"/><name val="等线"/></font>
            <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="等线"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF4472C4"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FFD9E2F3"/></left><right style="thin"><color rgb="FFD9E2F3"/></right><top style="thin"><color rgb="FFD9E2F3"/></top><bottom style="thin"><color rgb="FFD9E2F3"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="3">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """);

    private static XDocument CoreProperties(string title)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                cp + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "dc", dc),
                new XAttribute(XNamespace.Xmlns + "dcterms", dcterms),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement(dc + "title", title),
                new XElement(dc + "creator", "回天 AI"),
                new XElement(cp + "lastModifiedBy", "回天 AI"),
                new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now),
                new XElement(dcterms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now)));
    }

    private static XDocument AppProperties(string sheetName)
    {
        XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        XNamespace vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                ep + "Properties",
                new XAttribute(XNamespace.Xmlns + "vt", vt),
                new XElement(ep + "Application", "回天 AI"),
                new XElement(ep + "DocSecurity", 0),
                new XElement(ep + "HeadingPairs",
                    new XElement(vt + "vector", new XAttribute("size", 2), new XAttribute("baseType", "variant"),
                        new XElement(vt + "variant", new XElement(vt + "lpstr", "工作表")),
                        new XElement(vt + "variant", new XElement(vt + "i4", 1)))),
                new XElement(ep + "TitlesOfParts",
                    new XElement(vt + "vector", new XAttribute("size", 1), new XAttribute("baseType", "lpstr"),
                        new XElement(vt + "lpstr", sheetName)))));
    }

    private static void WriteEntry(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string Normalize(string value) => string.Concat(
        value.Where(character => !char.IsWhiteSpace(character)));
}

internal sealed record SpreadsheetWriteResult(bool Success, string Detail, string Path);
