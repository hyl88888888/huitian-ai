using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

/// <summary>
/// Hard completion gate. A successful intermediate tool or an App Server
/// completed event is evidence, never a substitute for the final thing
/// promised to the user.
/// </summary>
internal static class DeliverableCompletionVerifier
{
    private static readonly HashSet<string> ResearchCapabilities = new(
        [
            "huitian_search_web", "huitian_search_files",
            "huitian_read_text_file", "huitian_read_document",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ResponseCapabilities = new(
        ["assistant_response", "huitian_respond"],
        StringComparer.Ordinal);

    public static DeliverableVerificationResult Verify(
        IReadOnlyList<IntentDeliverableRequirement> requirements,
        IReadOnlyList<DeliverableExecutionEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(evidence);

        if (requirements.Count == 0)
        {
            return new DeliverableVerificationResult(
                false,
                [],
                ["任务没有声明最终交付物，不能把协议回合结束当作用户目标完成。"]);
        }

        var verified = new List<string>();
        var missing = new List<string>();
        var usedArtifactPaths = new Dictionary<string, List<IntentDeliverableRequirement>>(StringComparer.OrdinalIgnoreCase);
        var usedStateEvidence = new HashSet<int>();

        foreach (var requirement in requirements)
        {
            if (TryVerifyRequirement(
                    requirement,
                    evidence,
                    usedArtifactPaths,
                    usedStateEvidence,
                    out var evidenceText))
            {
                verified.Add(evidenceText);
            }
            else
            {
                missing.Add(requirement.Description);
            }
        }

        return new DeliverableVerificationResult(
            missing.Count == 0,
            verified,
            missing);
    }

    private static bool TryVerifyRequirement(
        IntentDeliverableRequirement requirement,
        IReadOnlyList<DeliverableExecutionEvidence> evidence,
        IDictionary<string, List<IntentDeliverableRequirement>> usedArtifactPaths,
        ISet<int> usedStateEvidence,
        out string evidenceText)
    {
        evidenceText = string.Empty;
        if (IsArtifactRequirement(requirement.Kind))
        {
            var artifact = FindArtifact(requirement, evidence, usedArtifactPaths);
            if (artifact is null)
            {
                return false;
            }

            if (!usedArtifactPaths.TryGetValue(artifact.Path, out var claims))
                usedArtifactPaths[artifact.Path] = claims = [];
            claims.Add(requirement);
            evidenceText = $"已核验交付物：{requirement.Description}；{artifact.Path}";
            return true;
        }

        if (requirement.Kind == IntentDeliverableKind.ResearchResult)
        {
            // Search and read results are source material. A usable research
            // result also needs a synthesized response for the user.
            var source = FindEvidence(evidence, usedStateEvidence, item =>
                IsUsable(item) &&
                ResearchCapabilities.Contains(item.Capability) &&
                !string.Equals(item.ResultCode, "no_results", StringComparison.Ordinal));
            var response = evidence.FirstOrDefault(item =>
                IsUsable(item) &&
                ResponseCapabilities.Contains(item.Capability) &&
                !string.IsNullOrWhiteSpace(item.RuntimeText));
            if (source.item is null || response is null)
            {
                return false;
            }

            usedStateEvidence.Add(source.index);
            evidenceText = $"已核验交付物：{requirement.Description}（资料已取得并形成答复）";
            return true;
        }

        Func<DeliverableExecutionEvidence, bool> predicate = requirement.Kind switch
        {
            IntentDeliverableKind.ConversationalAnswer => item =>
                IsUsable(item) &&
                ResponseCapabilities.Contains(item.Capability) &&
                !string.IsNullOrWhiteSpace(item.RuntimeText),
            IntentDeliverableKind.ApplicationState => item =>
                IsUsable(item) && item.Capability is
                    "huitian_open_application" or "huitian_open_browser" or
                    "huitian_activate_window" or "huitian_click_control" or
                    "huitian_type_text" or "huitian_scroll_window" or
                    "huitian_click_point" or "huitian_type_active" or
                    "huitian_press_keys",
            IntentDeliverableKind.ExternalDelivery => item =>
                IsUsable(item) &&
                (item.Capability is
                    "huitian_send_chat_message" or "huitian_send_chat_files") &&
                DestinationMatches(requirement.Destination, item.ArgumentsJson) &&
                (item.Capability == "huitian_send_chat_message" ||
                 SentFilesMatchVerifiedArtifacts(
                     item.ArgumentsJson,
                     evidence.Where(IsUsable).SelectMany(entry => entry.ArtifactPaths))),
            _ => _ => false,
        };
        var match = FindEvidence(evidence, usedStateEvidence, predicate);
        if (match.item is null)
        {
            return false;
        }

        usedStateEvidence.Add(match.index);
        evidenceText = $"已核验交付物：{requirement.Description}（{match.item.Capability}）";
        return true;
    }

    private static (DeliverableExecutionEvidence? item, int index) FindEvidence(
        IReadOnlyList<DeliverableExecutionEvidence> evidence,
        ISet<int> used,
        Func<DeliverableExecutionEvidence, bool> predicate)
    {
        for (var index = 0; index < evidence.Count; index++)
        {
            if (!used.Contains(index) && predicate(evidence[index]))
            {
                return (evidence[index], index);
            }
        }

        return (null, -1);
    }

    private static ArtifactMatch? FindArtifact(
        IntentDeliverableRequirement requirement,
        IReadOnlyList<DeliverableExecutionEvidence> evidence,
        IDictionary<string, List<IntentDeliverableRequirement>> usedPaths)
    {
        for (var evidenceIndex = 0; evidenceIndex < evidence.Count; evidenceIndex++)
        {
            var item = evidence[evidenceIndex];
            if (!IsUsable(item))
            {
                continue;
            }

            if (requirement.Kind == IntentDeliverableKind.ModifiedFile &&
                (!string.Equals(item.Capability, "huitian_modify_file", StringComparison.Ordinal) ||
                 !string.Equals(item.ResultCode, "modified_file_verified", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var candidate in item.ArtifactPaths)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string path;
                try
                {
                    path = Path.GetFullPath(candidate);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    continue;
                }

                // An explicit semantic identity permits multiple properties of
                // ONE artifact to be verified. Without it, outputs stay distinct.
                // Each property still requires its own evidence checks above.
                if ((usedPaths.TryGetValue(path, out var claims) &&
                     (string.IsNullOrWhiteSpace(requirement.ArtifactKey) ||
                      claims.Any(claim => !string.Equals(claim.ArtifactKey, requirement.ArtifactKey, StringComparison.Ordinal)))) ||
                    !ArtifactMatchesRequirement(path, requirement))
                {
                    continue;
                }

                return new ArtifactMatch(evidenceIndex, path);
            }
        }

        return null;
    }

    private static bool ArtifactMatchesRequirement(
        string path,
        IntentDeliverableRequirement requirement)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var requestedExtension = NormalizeExtension(requirement.Format);
        if (requestedExtension is not null &&
            !extension.Equals(requestedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allowed = AllowedExtensions(requirement.Kind);
        if (allowed is not null && !allowed.Contains(extension))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                return false;
            }

            return extension switch
            {
                ".docx" => VerifyOfficePackage(
                    path,
                    "word/document.xml",
                    entry => HasNonWhitespaceXmlText(
                        entry,
                        "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
                        "t")),
                ".xlsx" => VerifyOfficePackage(
                    path,
                    "xl/workbook.xml",
                    (_, archive) => archive.Entries.Any(entry =>
                        entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))),
                ".pptx" => VerifyOfficePackage(
                    path,
                    "ppt/presentation.xml",
                    (_, archive) => archive.Entries.Any(entry =>
                        entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))),
                ".pdf" => VerifyPdf(path, info.Length),
                ".png" => VerifyPrefix(path, [0x89, 0x50, 0x4E, 0x47]),
                ".jpg" or ".jpeg" => VerifyPrefix(path, [0xFF, 0xD8, 0xFF]),
                ".gif" => VerifyTextPrefix(path, "GIF8"),
                _ => info.Length > 0,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool VerifyOfficePackage(
        string path,
        string requiredEntry,
        Func<ZipArchiveEntry, bool> predicate) =>
        VerifyOfficePackage(path, requiredEntry, (entry, _) => predicate(entry));

    private static bool VerifyOfficePackage(
        string path,
        string requiredEntry,
        Func<ZipArchiveEntry, ZipArchive, bool> predicate)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(requiredEntry);
        return entry is { Length: > 0 } && predicate(entry, archive);
    }

    private static bool HasNonWhitespaceXmlText(
        ZipArchiveEntry entry,
        string namespaceName,
        string localName)
    {
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var xmlNamespace = (XNamespace)namespaceName;
        // Text in an arbitrary XML root is not a Word document. Reject malformed
        // output at the completion boundary so the agent can repair/retry it.
        if (document.Root?.Name != xmlNamespace + "document") return false;
        var body = document.Root.Element(xmlNamespace + "body");
        return body is not null && body.Descendants(xmlNamespace + localName)
            .Any(node => !string.IsNullOrWhiteSpace(node.Value));
    }

    private static bool VerifyPdf(string path, long length) =>
        length > 100 && VerifyTextPrefix(path, "%PDF-");

    private static bool DestinationMatches(string? destination, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string ReadIdentity(string name) => document.RootElement.TryGetProperty(name, out var node) &&
                node.ValueKind == JsonValueKind.String ? NormalizeIdentity(node.GetString()) : string.Empty;
            var contact = ReadIdentity("contact");
            var recipient = ReadIdentity("recipient");
            var platform = ReadIdentity("platform");
            // A shared platform or a substring of a person's name proves no
            // recipient identity. Conflicting aliases must fail closed too.
            if (contact.Length > 0 && recipient.Length > 0 &&
                !string.Equals(contact, recipient, StringComparison.OrdinalIgnoreCase)) return false;
            var actual = contact.Length > 0 ? contact : recipient;
            if (actual.Length == 0) return false;
            var expected = NormalizeIdentity(destination);
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ||
                   (platform.Length > 0 &&
                    (string.Equals(expected, platform + "/" + actual, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(expected, actual + "/" + platform, StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeIdentity(string? value) => (value ?? string.Empty).Trim();

    private static bool SentFilesMatchVerifiedArtifacts(
        string? argumentsJson,
        IEnumerable<string> verifiedArtifacts)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("filePaths", out var paths) ||
                paths.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var sent = paths.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => Path.GetFullPath(item!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sent.Count == 0 || sent.Any(path => !File.Exists(path))) return false;
            var candidates = verifiedArtifacts
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return candidates.Count == 0 || sent.Overlaps(candidates);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool VerifyTextPrefix(string path, string expected) =>
        VerifyPrefix(path, Encoding.ASCII.GetBytes(expected));

    private static bool VerifyPrefix(string path, IReadOnlyList<byte> expected)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < expected.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (stream.ReadByte() != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsable(DeliverableExecutionEvidence item) =>
        item.Success && !item.RequiresVerification;

    private static bool IsArtifactRequirement(IntentDeliverableKind kind) => kind is
        IntentDeliverableKind.Document or IntentDeliverableKind.Spreadsheet or
        IntentDeliverableKind.Presentation or IntentDeliverableKind.Image or
        IntentDeliverableKind.Audio or IntentDeliverableKind.Video or
        IntentDeliverableKind.File or IntentDeliverableKind.ModifiedFile;

    private static HashSet<string>? AllowedExtensions(IntentDeliverableKind kind) => kind switch
    {
        IntentDeliverableKind.Document => NewExtensions(".docx", ".doc", ".pdf", ".rtf", ".odt"),
        IntentDeliverableKind.Spreadsheet => NewExtensions(".xlsx", ".xls", ".csv", ".tsv", ".ods"),
        IntentDeliverableKind.Presentation => NewExtensions(".pptx", ".ppt", ".odp"),
        IntentDeliverableKind.Image => NewExtensions(".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"),
        IntentDeliverableKind.Audio => NewExtensions(".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg"),
        IntentDeliverableKind.Video => NewExtensions(".mp4", ".mov", ".mkv", ".avi", ".webm"),
        _ => null,
    };

    private static HashSet<string> NewExtensions(params string[] extensions) =>
        new(extensions, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeExtension(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) ||
            format.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var clean = format.Trim().TrimStart('.').ToLowerInvariant();
        clean = clean switch
        {
            "word" or "microsoft word" => "docx",
            "excel" or "microsoft excel" => "xlsx",
            "powerpoint" or "microsoft powerpoint" => "pptx",
            _ => clean,
        };
        return "." + clean;
    }

    private sealed record ArtifactMatch(int EvidenceIndex, string Path);
}

internal sealed record DeliverableExecutionEvidence(
    string Capability,
    bool Success,
    bool RequiresVerification,
    IReadOnlyList<string> ArtifactPaths,
    string? RuntimeText = null,
    string? ResultCode = null,
    string? ArgumentsJson = null);

internal sealed record DeliverableVerificationResult(
    bool Succeeded,
    IReadOnlyList<string> Verified,
    IReadOnlyList<string> Missing);
