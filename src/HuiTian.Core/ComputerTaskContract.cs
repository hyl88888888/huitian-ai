using System.Globalization;
using System.Text.Json;

namespace HuiTian.Core;

/// <summary>
/// Describes how a computer task behaves without prescribing a specific tool
/// or application implementation.
/// </summary>
public enum ComputerTaskMode
{
    Observe,
    Operate,
    Mixed,
    Monitor,
}

public enum ComputerTaskRisk
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

public enum CompletionEvidenceKind
{
    ReturnValue,
    StateObservation,
    Artifact,
    UserConfirmation,
}

/// <summary>
/// Stable operation identifiers used by the route adapter. Application-specific
/// contracts may use more precise operation ids.
/// </summary>
public static class ComputerTaskOperations
{
    public const string Research = "research";
    public const string FileWork = "file-work";
    public const string Presentation = "presentation";
    public const string ComputerControl = "computer-control";
    public const string Composite = "composite";
}

/// <summary>
/// An observable condition that an executor must satisfy before reporting a
/// task as complete.
/// </summary>
public sealed class CompletionEvidenceRequirement
{
    public CompletionEvidenceRequirement(
        CompletionEvidenceKind kind,
        string description)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Description = RequireText(description, nameof(description));
    }

    public CompletionEvidenceKind Kind { get; }

    public string Description { get; }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }
}

/// <summary>
/// Carries unresolved target or intent information without forcing a parser or
/// UI policy into the task contract.
/// </summary>
public sealed class ComputerTaskAmbiguity
{
    public static ComputerTaskAmbiguity None { get; } = new(false);

    public ComputerTaskAmbiguity(
        bool requiresClarification,
        string? reason = null,
        IReadOnlyList<string>? candidates = null,
        string? clarificationQuestion = null)
    {
        Reason = NormalizeOptionalText(reason);
        ClarificationQuestion = NormalizeOptionalText(clarificationQuestion);
        if (requiresClarification && Reason is null && ClarificationQuestion is null)
        {
            throw new ArgumentException(
                "An ambiguous task must include a reason or clarification question.",
                nameof(reason));
        }

        RequiresClarification = requiresClarification;
        Candidates = NormalizeTextList(candidates);
    }

    public bool RequiresClarification { get; }

    public string? Reason { get; }

    public IReadOnlyList<string> Candidates { get; }

    public string? ClarificationQuestion { get; }

    public static ComputerTaskAmbiguity Unresolved(
        string reason,
        IReadOnlyList<string>? candidates = null,
        string? clarificationQuestion = null) =>
        new(true, reason, candidates, clarificationQuestion);

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IReadOnlyList<string> NormalizeTextList(
        IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Array.AsReadOnly(normalized);
    }
}

/// <summary>
/// A channel-neutral contract produced by routing and consumed by executors.
/// It deliberately contains no natural-language parsing rules and no Windows
/// implementation details.
/// </summary>
public sealed class ComputerTaskContract
{
    public ComputerTaskContract(
        ComputerTaskMode mode,
        string goal,
        string operation,
        string? target = null,
        string? app = null,
        string? source = null,
        IReadOnlyList<string>? constraints = null,
        IReadOnlyList<CompletionEvidenceRequirement>? completionEvidence = null,
        ComputerTaskRisk risk = ComputerTaskRisk.Low,
        TimeSpan? duration = null,
        string? stopCondition = null,
        ComputerTaskAmbiguity? ambiguity = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (duration is { } maximumDuration && maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be greater than zero when provided.");
        }

        Mode = mode;
        Goal = RequireText(goal, nameof(goal));
        Target = NormalizeOptionalText(target);
        App = NormalizeOptionalText(app);
        Source = NormalizeOptionalText(source);
        Operation = RequireText(operation, nameof(operation));
        Constraints = ComputerTaskAmbiguity.NormalizeTextList(constraints);
        CompletionEvidence = CopyEvidence(completionEvidence);
        Risk = risk;
        Duration = duration;
        StopCondition = NormalizeOptionalText(stopCondition);
        Ambiguity = ambiguity ?? ComputerTaskAmbiguity.None;
    }

    public ComputerTaskMode Mode { get; }

    public string Goal { get; }

    public string? Target { get; }

    public string? App { get; }

    public string? Source { get; }

    public string Operation { get; }

    public IReadOnlyList<string> Constraints { get; }

    public IReadOnlyList<CompletionEvidenceRequirement> CompletionEvidence { get; }

    public ComputerTaskRisk Risk { get; }

    public TimeSpan? Duration { get; }

    public string? StopCondition { get; }

    public ComputerTaskAmbiguity Ambiguity { get; }

    /// <summary>
    /// True only when the contract is bounded, unambiguous, and declares how
    /// completion will be verified.
    /// </summary>
    public bool IsReadyForExecution => GetExecutionBlockers().Count == 0;

    /// <summary>
    /// Maps an already-decided ConversationPolicy route to a conservative
    /// contract. It does not inspect or parse the goal text, so application,
    /// target, duration, and stop condition remain unspecified.
    /// </summary>
    public static ComputerTaskContract FromRoute(
        AgentTaskRoute route,
        string goal)
    {
        (ComputerTaskMode Mode,
         string Operation,
         ComputerTaskRisk Risk,
         CompletionEvidenceRequirement Evidence) defaults = route switch
        {
            AgentTaskRoute.Research =>
                (
                    ComputerTaskMode.Observe,
                    ComputerTaskOperations.Research,
                    ComputerTaskRisk.Low,
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.ReturnValue,
                        "The requested research result is available.")),
            AgentTaskRoute.FileWork =>
                (
                    ComputerTaskMode.Operate,
                    ComputerTaskOperations.FileWork,
                    ComputerTaskRisk.Medium,
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.Artifact,
                        "The requested file result exists at its intended location.")),
            AgentTaskRoute.Presentation =>
                (
                    ComputerTaskMode.Operate,
                    ComputerTaskOperations.Presentation,
                    ComputerTaskRisk.Medium,
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.Artifact,
                        "The requested presentation exists and can be opened.")),
            AgentTaskRoute.ComputerControl =>
                (
                    ComputerTaskMode.Operate,
                    ComputerTaskOperations.ComputerControl,
                    ComputerTaskRisk.Medium,
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.StateObservation,
                        "The requested application state is observed after the operation.")),
            AgentTaskRoute.Composite =>
                (
                    ComputerTaskMode.Mixed,
                    ComputerTaskOperations.Composite,
                    ComputerTaskRisk.Medium,
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.StateObservation,
                        "Every requested stage has observable completion evidence.")),
            AgentTaskRoute.DirectAnswer => throw new ArgumentException(
                "Direct-answer routes do not create computer tasks.",
                nameof(route)),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        return new ComputerTaskContract(
            defaults.Mode,
            goal,
            defaults.Operation,
            source: "conversation",
            completionEvidence: [defaults.Evidence],
            risk: defaults.Risk);
    }

    /// <summary>
    /// Produces a stable, data-only representation suitable for appending to an
    /// agent turn. User text is JSON-escaped and never interpreted here.
    /// </summary>
    public string FormatForAgent() => JsonSerializer.Serialize(
        new
        {
            mode = ToAgentValue(Mode),
            goal = Goal,
            target = Target,
            app = App,
            source = Source,
            operation = Operation,
            constraints = Constraints,
            completion_evidence = CompletionEvidence.Select(item => new
            {
                kind = ToAgentValue(item.Kind),
                description = item.Description,
            }),
            risk = Risk.ToString().ToLowerInvariant(),
            duration = Duration?.ToString("c", CultureInfo.InvariantCulture),
            stop_condition = StopCondition,
            ambiguity = new
            {
                requires_clarification = Ambiguity.RequiresClarification,
                reason = Ambiguity.Reason,
                candidates = Ambiguity.Candidates,
                clarification_question = Ambiguity.ClarificationQuestion,
            },
        },
        AgentJsonOptions);

    public IReadOnlyList<string> GetExecutionBlockers()
    {
        var blockers = new List<string>();
        if (Ambiguity.RequiresClarification)
        {
            blockers.Add(
                Ambiguity.ClarificationQuestion ??
                Ambiguity.Reason ??
                "The task requires clarification.");
        }

        if (CompletionEvidence.Count == 0)
        {
            blockers.Add("At least one completion-evidence requirement is required.");
        }

        if (Mode == ComputerTaskMode.Monitor &&
            Duration is null &&
            StopCondition is null)
        {
            blockers.Add("A monitoring task requires a duration or stop condition.");
        }

        return Array.AsReadOnly(blockers.ToArray());
    }

    private static IReadOnlyList<CompletionEvidenceRequirement> CopyEvidence(
        IReadOnlyList<CompletionEvidenceRequirement>? evidence)
    {
        if (evidence is not { Count: > 0 })
        {
            return Array.Empty<CompletionEvidenceRequirement>();
        }

        if (evidence.Any(item => item is null))
        {
            throw new ArgumentException(
                "Completion evidence cannot contain null entries.",
                nameof(evidence));
        }

        return Array.AsReadOnly(evidence.ToArray());
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToAgentValue(ComputerTaskMode mode) => mode switch
    {
        ComputerTaskMode.Observe => "observe",
        ComputerTaskMode.Operate => "operate",
        ComputerTaskMode.Mixed => "mixed",
        ComputerTaskMode.Monitor => "monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string ToAgentValue(CompletionEvidenceKind kind) => kind switch
    {
        CompletionEvidenceKind.ReturnValue => "return_value",
        CompletionEvidenceKind.StateObservation => "state_observation",
        CompletionEvidenceKind.Artifact => "artifact",
        CompletionEvidenceKind.UserConfirmation => "user_confirmation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static readonly JsonSerializerOptions AgentJsonOptions = new()
    {
        WriteIndented = false,
    };
}
