using System.IO.Compression;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

if (args.Length == 2 && args[0] == "--read-pptx")
{
    // Read-only performance probe: no Office process and no user-file writes.
    var source = Path.GetFullPath(args[1]);
    using var before = File.OpenRead(source);
    var fingerprint = System.Security.Cryptography.SHA256.HashData(before);
    before.Dispose();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var excerpt = OpenXmlPresentationEditor.ReadText(source, 1, 2, 2000);
    timer.Stop();
    using var after = File.OpenRead(source);
    Require(excerpt.Success && excerpt.ResultCode == "document_excerpt", "read-only excerpt failed");
    Require(fingerprint.SequenceEqual(System.Security.Cryptography.SHA256.HashData(after)), "source changed");
    Console.WriteLine($"PASS real PPTX read-only excerpt: {timer.ElapsedMilliseconds}ms; {excerpt.Text.Length} chars; source preserved; no Office launch.");
    return;
}

if (args.Contains("--verification-boundary"))
{
    VerificationBoundaryRegression.Run();
    return;
}

PresentationFormattingRegression.Run();

var bridge = new WindowsToolBridge();
var spreadsheet = await bridge.ExecuteAsync(
    "huitian_create_spreadsheet",
    JsonSerializer.SerializeToElement(new
    {
        title = "未见表达销售复盘",
        sheetName = "九月数据",
        headers = new[] { "地区", "订单数", "销售额", "客单价" },
        rows = new object?[][]
        {
            ["华北", 12, 3600.5, "=C3/B3"],
            ["华东", 20, 7200, "=C4/B4"],
        },
    }),
    CancellationToken.None);
Require(spreadsheet.Success && spreadsheet.ResultCode == "artifact_verified",
    $"spreadsheet creation failed: {spreadsheet.Text}");
var workbookPath = spreadsheet.ArtifactPaths?.Single()
                   ?? throw new InvalidDataException("spreadsheet artifact path missing");
Require(File.Exists(workbookPath), "workbook was not created");
using (var archive = ZipFile.OpenRead(workbookPath))
{
    Require(archive.GetEntry("xl/workbook.xml") is not null, "workbook.xml missing");
    Require(archive.GetEntry("xl/worksheets/sheet1.xml") is not null, "sheet1.xml missing");
}

var testRoot = @"D:\HuiTian\UserContent\测试";
Directory.CreateDirectory(testRoot);
var sourcePath = Path.Combine(testRoot, $"modify-source-{Guid.NewGuid():N}.txt");
await File.WriteAllTextAsync(sourcePath, "会议时间：上午八点。原件需要保留。");
var modified = await bridge.ExecuteAsync(
    "huitian_modify_file",
    JsonSerializer.SerializeToElement(new
    {
        path = sourcePath,
        replacements = new[] { new { find = "上午八点", replace = "上午九点" } },
    }),
    CancellationToken.None);
Require(modified.Success && modified.ResultCode == "modified_file_verified",
    $"file modification failed: {modified.Text}");
var modifiedPath = modified.ArtifactPaths?.Single()
                   ?? throw new InvalidDataException("modified artifact path missing");
Require(await File.ReadAllTextAsync(sourcePath) == "会议时间：上午八点。原件需要保留。",
    "source file was overwritten");
Require((await File.ReadAllTextAsync(modifiedPath)).Contains("上午九点", StringComparison.Ordinal),
    "modified copy does not contain the requested change");

var requirements = new[]
{
    new IntentDeliverableRequirement(
        IntentDeliverableKind.Spreadsheet,
        "真实工作簿",
        "xlsx"),
    new IntentDeliverableRequirement(
        IntentDeliverableKind.ExternalDelivery,
        "同一工作簿送达联系人",
        Destination: "测试联系人"),
};
var evidence = new[]
{
    new DeliverableExecutionEvidence(
        "huitian_create_spreadsheet",
        true,
        false,
        [workbookPath],
        ResultCode: "artifact_verified",
        ArgumentsJson: "{}"),
    new DeliverableExecutionEvidence(
        "huitian_send_chat_files",
        true,
        false,
        [],
        ResultCode: "sent_verified",
        ArgumentsJson: JsonSerializer.Serialize(new
        {
            platform = "微信",
            contact = "测试联系人",
            filePaths = new[] { workbookPath },
        })),
};
Require(DeliverableCompletionVerifier.Verify(requirements, evidence).Succeeded,
    "generated-then-send evidence should close both deliverables");
var wrongFile = Path.Combine(testRoot, $"wrong-{Guid.NewGuid():N}.txt");
await File.WriteAllTextAsync(wrongFile, "not the generated workbook");
var wrongEvidence = evidence.ToArray();
wrongEvidence[1] = wrongEvidence[1] with
{
    ArgumentsJson = JsonSerializer.Serialize(new
    {
        platform = "微信",
        contact = "测试联系人",
        filePaths = new[] { wrongFile },
    }),
};
Require(!DeliverableCompletionVerifier.Verify(requirements, wrongEvidence).Succeeded,
    "a different sent file must not satisfy generated-then-send completion");

Console.WriteLine($"PASS: XLSX verified => {workbookPath}");
Console.WriteLine($"PASS: modified copy verified => {modifiedPath}");
Console.WriteLine("PASS: generated-then-send requires the same verified artifact");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
