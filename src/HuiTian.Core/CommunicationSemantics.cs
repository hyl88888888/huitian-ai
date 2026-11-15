namespace HuiTian.Core;

/// <summary>
/// A named person's own goal and requested speaking role. Instructions for
/// one person must never be silently reused for another person.
/// </summary>
public sealed record CommunicationTargetInstruction(
    string Name,
    string Goal,
    string? Persona);

/// <summary>
/// The outcome the user actually requested when discovering new people.
/// It is extracted semantically, never inferred from a fixed phrase list.
/// </summary>
public enum SocialDiscoveryDesiredOutcome
{
    Unspecified,
    CandidateShortlist,
    ContactInitiated,
    ReciprocalConversation,
    RelationshipEstablished,
}

/// <summary>
/// Model-extracted communication slots. These are only proposed destinations;
/// the desktop adapter still has to identify and verify the actual account.
/// </summary>
public sealed record CommunicationTargetSlots(
    string Action,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> TargetNames,
    IReadOnlyList<string> ExcludedNames,
    bool FindNewPeople,
    bool UseCurrentConversation,
    string? Criteria,
    TimeSpan? RequestedDuration)
{
    public string? LiteralMessage { get; init; }

    public string? SharedPersona { get; init; }

    public IReadOnlyList<CommunicationTargetInstruction> TargetInstructions { get; init; } = [];

    public SocialDiscoveryDesiredOutcome DesiredOutcome { get; init; }

    public bool RequiresTargetSelection =>
        FindNewPeople || Platforms.Count != 1 ||
        (!UseCurrentConversation && TargetNames.Count != 1);

    public bool HasDistinctInstructionsForEveryTarget =>
        TargetNames.Count > 1 && TargetNames.Count == TargetInstructions.Count &&
        TargetNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == TargetNames.Count &&
        !TargetNames.Any(name => ExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) &&
        TargetNames.All(name => TargetInstructions.Count(instruction =>
            string.Equals(instruction.Name, name, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(instruction.Goal)) == 1);
}
