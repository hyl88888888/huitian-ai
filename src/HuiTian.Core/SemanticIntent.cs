namespace HuiTian.Core;

/// <summary>
/// A compact semantic decision made before the full agent starts.  This is
/// deliberately about user intent, not about implementation details or tool
/// names, so alternate phrasings can share the same execution route.
/// </summary>
public enum SemanticIntentMode
{
    Answer,
    Operate,
    Mixed,
    Clarify,
}

public enum SemanticIntentDomain
{
    Other,
    Chat,
    Application,
    File,
    Browser,
    Office,
    Media,
    System,
    Research,
}

public sealed record SemanticIntentDecision(
    SemanticIntentMode Mode,
    SemanticIntentDomain Domain,
    double Confidence,
    long ElapsedMilliseconds,
    string Source)
{
    public bool IsConfident => Confidence >= 0.68;

    /// <summary>
    /// Optional communication slots extracted by the same model call that
    /// classified this turn. Missing or incomplete slots require a separate
    /// resolver call; they never authorize a guessed recipient.
    /// </summary>
    public CommunicationTargetSlots? CommunicationTargets { get; init; }
}
