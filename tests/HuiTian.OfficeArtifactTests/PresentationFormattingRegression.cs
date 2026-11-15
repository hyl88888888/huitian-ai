using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using HuiTian.Infrastructure;
using HuiTian.Core;

internal static class PresentationFormattingRegression
{
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static void Run()
    {
        var folder = @"D:\HuiTian\Cache\Tests\presentation-format-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "fixture.pptx");
        CreateFixture(source, 500);
        var fingerprint = SHA256.HashData(File.ReadAllBytes(source));
        var timer = Stopwatch.StartNew();
        var result = OfficeArtifactService.ModifyFile(JsonSerializer.SerializeToElement(new
        {
            path = source,
            presentationStyle = new { backgroundColor = "#FFFFFF", fontColor = "000000" },
        }));
        var elapsed = timer.ElapsedMilliseconds;
        Check(result.Success, result.Text);
        Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(fingerprint), "original changed");
        var output = result.ArtifactPaths!.Single();
        Check(DeliverableCompletionVerifier.Verify(
            [new(IntentDeliverableKind.Presentation, "修改后的演示稿", "pptx", ArtifactKey: "edited-deck"), new(IntentDeliverableKind.ModifiedFile, "已修改副本", "pptx", ArtifactKey: "edited-deck")],
            [new DeliverableExecutionEvidence("huitian_modify_file", true, false, [output], ResultCode: result.ResultCode, ArgumentsJson: "{}")]).Succeeded,
            "a verified PPT modification must satisfy both presentation and modified-file deliverables");
        var modificationEvidence = new[] { new DeliverableExecutionEvidence("huitian_modify_file", true, false, [output], ResultCode: result.ResultCode, ArgumentsJson: "{}") };
        Check(!DeliverableCompletionVerifier.Verify(
            [new(IntentDeliverableKind.Presentation, "第一份", "pptx", ArtifactKey: "one"), new(IntentDeliverableKind.Presentation, "第二份", "pptx", ArtifactKey: "two")],
            modificationEvidence).Succeeded, "one file must not satisfy two different identities");
        Check(!DeliverableCompletionVerifier.Verify(
            [new(IntentDeliverableKind.Presentation, "新建演示稿", "pptx"), new(IntentDeliverableKind.ModifiedFile, "另一个已有文件", "pptx")],
            modificationEvidence).Succeeded, "unlinked requirements must remain distinct");
        Check(!DeliverableCompletionVerifier.Verify(
            [new(IntentDeliverableKind.Presentation, "演示稿", "pptx", ArtifactKey: "same"), new(IntentDeliverableKind.ModifiedFile, "修改", "pptx", ArtifactKey: "same")],
            [modificationEvidence[0] with { Capability = "huitian_create_presentation", ResultCode = "artifact_verified" }]).Succeeded,
            "shared identity must not substitute creation evidence for a verified modification");
        Check(DeliverableCompletionVerifier.Verify(
            [new(IntentDeliverableKind.ModifiedFile, "修改", "pptx", ArtifactKey: "same"), new(IntentDeliverableKind.Presentation, "演示稿", "pptx", ArtifactKey: "same")],
            modificationEvidence).Succeeded, "shared-artifact verification must be order independent");
        using (var zip = ZipFile.OpenRead(output))
        {
            var first = Read(zip, "ppt/slides/slide1.xml");
            Check((string?)first.Root?.Element(XName.Get("cSld", P))?.Element(XName.Get("bg", P))?
                .Descendants(XName.Get("srgbClr", A)).Single().Attribute("val") == "FFFFFF", "background not applied");
            Check(first.Descendants(XName.Get("t", A)).Select(n => n.Value).SequenceEqual(new[] { "第一", "测试", "表格文字" }), "text content lost");
            Check(first.Descendants(XName.Get("rPr", A)).All(n => (string?)n.Descendants(XName.Get("srgbClr", A)).First().Attribute("val") == "000000"), "run font missing");
            Check(Read(zip, "ppt/slideMasters/slideMaster1.xml").Descendants(XName.Get("defRPr", A))
                .All(n => (string?)n.Descendants(XName.Get("srgbClr", A)).First().Attribute("val") == "000000"), "master font missing");
            using var media = new StreamReader(zip.GetEntry("ppt/media/fixture.bin")!.Open());
            Check(media.ReadToEnd() == "preserve-media-byte-for-byte", "media changed");
        }
        var partial = OfficeArtifactService.ModifyFile(JsonSerializer.SerializeToElement(new
        {
            path = source,
            presentationStyle = new { slides = new[] { 2 }, backgroundColor = "112233", fontColor = "FFEEDD", fontFamily = "等线", fontSize = 24, bold = true },
        }));
        Check(partial.Success, partial.Text);
        using (var zip = ZipFile.OpenRead(partial.ArtifactPaths!.Single()))
        {
            // The fixture's presentation order is deliberately reversed.
            var selected = Read(zip, "ppt/slides/slide499.xml");
            Check(selected.Descendants(XName.Get("rPr", A)).All(n => (string?)n.Attribute("b") == "1" && (int?)n.Attribute("sz") == 2400), "page selection/style wrong");
            Check(!Read(zip, "ppt/slides/slide1.xml").Descendants(XName.Get("bg", P)).Any(), "unselected page changed");
            Check(!Read(zip, "ppt/slideMasters/slideMaster1.xml").Descendants(XName.Get("solidFill", A)).Any(), "shared master changed for one page");
        }
        var replacement = OfficeArtifactService.ModifyFile(JsonSerializer.SerializeToElement(new
        {
            path = source, replacements = new[] { new { find = "第一测试", replace = "替换跨段文字" } },
        }));
        Check(replacement.Success, replacement.Text);
        using (var zip = ZipFile.OpenRead(replacement.ArtifactPaths!.Single()))
            Check(string.Concat(Read(zip, "ppt/slides/slide1.xml").Descendants(XName.Get("t", A)).Select(n => n.Value)).Contains("替换跨段文字"), "cross-run replacement failed");
        foreach (var style in new object[] { new { backgroundColor = "white" }, new { slides = new[] { 501 }, fontColor = "000000" }, new { }, new { fontSize = -1 } })
            Check(!OfficeArtifactService.ModifyFile(JsonSerializer.SerializeToElement(new { path = source, presentationStyle = style })).Success, "invalid style accepted");
        var excerpt = OpenXmlPresentationEditor.ReadText(source, 2, 2, 1000);
        Check(excerpt.Success && excerpt.Text.Contains("第 2 页") && !excerpt.Text.Contains("第 4 页") && excerpt.Text.Contains("startPage=4"), "bounded page reading failed");
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { slides = 500, elapsedMilliseconds = elapsed,
            result.Success, sourcePreserved = true, output, note = "Synthetic package regression; not a live PowerPoint visual validation." }));
        Console.WriteLine($"PASS PPT formatting: 500 slides in {elapsed} ms; global/selected styles, cross-run text, invalid arguments, bounded reads, original/media/order preserved. {folder}");
    }

    private static XDocument Read(ZipArchive zip, string path) { using var stream = zip.GetEntry(path)!.Open(); return XDocument.Load(stream); }
    private static void Check(bool ok, string detail) { if (!ok) throw new InvalidDataException(detail); }
    private static void CreateFixture(string path, int count)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Put("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
        Put("ppt/presentation.xml", $"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldIdLst>" +
            string.Concat(Enumerable.Range(1, count).Reverse().Select(i => $"<p:sldId id='{i + 255}' r:id='rId{i}'/>")) + "</p:sldIdLst></p:presentation>");
        Put("ppt/_rels/presentation.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
            string.Concat(Enumerable.Range(1, count).Select(i => $"<Relationship Id='rId{i}' Type='{R}/slide' Target='slides/slide{i}.xml'/>")) + "</Relationships>");
        for (var i = 1; i <= count; i++)
            Put($"ppt/slides/slide{i}.xml", $"<p:sld xmlns:p='{P}' xmlns:a='{A}'><p:cSld><p:spTree><p:grpSp><p:sp><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>第一</a:t></a:r><a:r><a:rPr b='0'/><a:t>测试</a:t></a:r></a:p></p:txBody></p:sp></p:grpSp><p:graphicFrame><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>表格文字</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame></p:spTree></p:cSld></p:sld>");
        Put("ppt/slideMasters/slideMaster1.xml", $"<p:sldMaster xmlns:p='{P}' xmlns:a='{A}'><p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz='3600'/></a:lvl1pPr></p:titleStyle></p:txStyles></p:sldMaster>");
        Put("ppt/media/fixture.bin", "preserve-media-byte-for-byte");
    }
}
