using System.IO.Compression;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class VerificationBoundaryRegression
{
    public static void Run()
    {
        var root = Path.Combine(@"D:\HuiTian\Cache\Tests", "verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        Check("matching platform cannot verify a different recipient", () => !VerifyRecipient("微信/测试甲", "测试乙", "微信").Succeeded);
        Check("a recipient substring is not identity", () => !VerifyRecipient("测试甲", "甲", "微信").Succeeded);
        Check("exact platform and recipient remain accepted", () => VerifyRecipient("微信/测试甲", "测试甲", "微信").Succeeded);
        Check("exact recipient remains accepted", () => VerifyRecipient("测试甲", "测试甲", "微信").Succeeded);
        Check("missing destination cannot prove a delivery", () => !VerifyRecipient("", "测试甲", "微信").Succeeded);
        Check("missing recipient cannot prove a delivery", () => !VerifyRecipient("微信", "", "微信").Succeeded);
        Check("punctuation is part of recipient identity", () => !VerifyRecipient("test.person", "testperson", "微信").Succeeded);
        Check("internal spaces are part of recipient identity", () => !VerifyRecipient("Test Person", "TestPerson", "微信").Succeeded);
        Check("malformed Word XML is rejected without throwing", () =>
        {
            var file = CreatePackage("broken.docx", "word/document.xml", "<broken");
            return !Verify(file, IntentDeliverableKind.Document).Succeeded;
        });
        Check("non-Word XML cannot impersonate a Word document", () =>
        {
            var file = CreatePackage("wrong-root.docx", "word/document.xml",
                "<fake xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:t>text</w:t></fake>");
            return !Verify(file, IntentDeliverableKind.Document).Succeeded;
        });
        Check("valid Word content remains accepted", () =>
        {
            var file = CreatePackage("valid.docx", "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Verified content</w:t></w:r></w:p></w:body></w:document>");
            return Verify(file, IntentDeliverableKind.Document).Succeeded;
        });
        Console.WriteLine($"Evidence directory: {root}");
        if (failures.Count > 0) throw new InvalidDataException(string.Join("; ", failures));

        void Check(string name, Func<bool> test)
        {
            try { if (!test()) throw new InvalidDataException("unexpected acceptance/rejection"); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL {name}: {ex.GetType().Name}"); }
        }
        string CreatePackage(string name, string entry, string xml)
        {
            var file = Path.Combine(root, name);
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(xml);
            return file;
        }
        static DeliverableVerificationResult Verify(string path, IntentDeliverableKind kind) =>
            DeliverableCompletionVerifier.Verify(
                [new IntentDeliverableRequirement(kind, "local test artifact")],
                [new DeliverableExecutionEvidence("huitian_create_document", true, false, [path], ResultCode: "artifact_verified")]);
        static DeliverableVerificationResult VerifyRecipient(string destination, string recipient, string platform) =>
            DeliverableCompletionVerifier.Verify(
                [new IntentDeliverableRequirement(IntentDeliverableKind.ExternalDelivery, "synthetic delivery receipt", Destination: destination)],
                [new DeliverableExecutionEvidence("huitian_send_chat_message", true, false, [], ResultCode: "sent_verified",
                    ArgumentsJson: System.Text.Json.JsonSerializer.Serialize(new { contact = recipient, platform }))]);
    }
}
