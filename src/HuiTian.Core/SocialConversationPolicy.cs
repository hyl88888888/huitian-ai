namespace HuiTian.Core;

/// <summary>
/// A platform's stable, verified profile identifier. A display name is not an identifier:
/// two people may have the same name and a person may rename their profile.
/// </summary>
public readonly record struct SocialContactKey(string Platform, string StableContactId)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Platform) &&
        !string.IsNullOrWhiteSpace(StableContactId) &&
        StableContactId.Length <= 256;

    public static SocialContactKey Create(string? platform, string? stableContactId)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("需要平台标识。", nameof(platform));
        if (string.IsNullOrWhiteSpace(stableContactId))
            throw new ArgumentException("需要已核验的稳定联系人 ID。", nameof(stableContactId));
        var key = new SocialContactKey(
            NormalizePlatform(platform),
            stableContactId.Trim());
        if (!key.IsValid)
            throw new ArgumentException("需要平台和已核验的稳定联系人 ID，不能只凭昵称建档。", nameof(stableContactId));
        return key;
    }

    /// <summary>Protocol/platform aliases only; this does not interpret user intent.</summary>
    public static string NormalizePlatform(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        var normalized = CommunicationPlatformIdentity.Canonicalize(platform) ??
                         platform.Trim().ToLowerInvariant();
        return normalized switch
        {
            "soul" or "soulapp" => "Soul",
            "douyin" or "抖音" => "抖音",
            var other => other,
        };
    }
}

public enum SocialAdultStatus
{
    Unknown,
    VerifiedAdult,
    Minor,
}

public enum SocialRelationshipStatus
{
    Unknown,
    NewPerson,
    ExistingContact,
    FamilyOrProtected,
}

public enum SocialMessagingCost
{
    Unknown,
    Free,
    Paid,
}

/// <summary>
/// Values must come from a current, observable account/profile state or explicit user input.
/// Do not infer adulthood, relationship, or free messaging from a photo or a name.
/// </summary>
public sealed record SocialCandidateEvidence(
    SocialContactKey Contact,
    string DisplayName,
    bool StableIdentityVerified,
    SocialAdultStatus AdultStatus,
    SocialRelationshipStatus RelationshipStatus,
    SocialMessagingCost MessagingCost,
    bool MessagingAvailable);

public enum SocialCandidateRejectionReason
{
    IdentityUnverified,
    AdultStatusUnknown,
    Underage,
    RelationshipUnknown,
    ExistingOrProtectedContact,
    PaidOrUnknownCost,
    MessagingUnavailable,
    AlreadyContacted,
}

/// <summary>
/// Eligibility means it is reasonable to prepare a draft for a NEW acquaintance.
/// It never grants authority to send, pay, follow, or claim to be someone else.
/// </summary>
public sealed record SocialCandidateDecision(
    bool EligibleForDraft,
    IReadOnlyList<SocialCandidateRejectionReason> Reasons);

public static class SocialCandidatePolicy
{
    public static SocialCandidateDecision EvaluateForNewOutreach(
        SocialCandidateEvidence candidate,
        bool alreadyContacted)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var reasons = new List<SocialCandidateRejectionReason>();
        if (!candidate.Contact.IsValid || !candidate.StableIdentityVerified)
            reasons.Add(SocialCandidateRejectionReason.IdentityUnverified);
        if (candidate.AdultStatus == SocialAdultStatus.Unknown)
            reasons.Add(SocialCandidateRejectionReason.AdultStatusUnknown);
        else if (candidate.AdultStatus == SocialAdultStatus.Minor)
            reasons.Add(SocialCandidateRejectionReason.Underage);
        if (candidate.RelationshipStatus == SocialRelationshipStatus.Unknown)
            reasons.Add(SocialCandidateRejectionReason.RelationshipUnknown);
        else if (candidate.RelationshipStatus != SocialRelationshipStatus.NewPerson)
            reasons.Add(SocialCandidateRejectionReason.ExistingOrProtectedContact);
        if (candidate.MessagingCost != SocialMessagingCost.Free)
            reasons.Add(SocialCandidateRejectionReason.PaidOrUnknownCost);
        if (!candidate.MessagingAvailable)
            reasons.Add(SocialCandidateRejectionReason.MessagingUnavailable);
        if (alreadyContacted)
            reasons.Add(SocialCandidateRejectionReason.AlreadyContacted);
        return new SocialCandidateDecision(reasons.Count == 0, reasons);
    }
}
