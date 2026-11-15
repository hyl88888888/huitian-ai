using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuiTian.Core;

/// <summary>
/// Semantic task kinds describe the result the user expects.  They are not
/// coupled to a phrase, application, tool, or Windows implementation.
/// </summary>
public enum SemanticTaskType
{
    Answer,
    Create,
    ComputerAction,
    ComputerTask,
    Hybrid,
    MultiAppTask,
    Clarify,
}

public enum IntentExecutionDomain
{
    Answer,
    Research,
    File,
    Office,
    Browser,
    Communication,
    Media,
    System,
    Development,
    Other,
    MultiApp,
}

public enum IntentEntityRole
{
    Object,
    Target,
    Recipient,
    Source,
    Location,
    Application,
    Person,
    File,
    Time,
    Quantity,
    ContextReference,
    Other,
}

public enum IntentRiskLevel
{
    None,
    Reversible,
    ExternalEffect,
    HighImpact,
}

public enum IntentEvidenceKind
{
    ReturnValue,
    StateObservation,
    Artifact,
    ExternalState,
    UserConfirmation,
}

/// <summary>
/// Describes the observable thing the user expects to receive.  This is kept
/// separate from actions: researching material is an action, while a printable
/// document built from that material is the final deliverable.
/// </summary>
public enum IntentDeliverableKind
{
    ConversationalAnswer,
    ResearchResult,
    Document,
    Spreadsheet,
    Presentation,
    Image,
    Audio,
    Video,
    File,
    ModifiedFile,
    ApplicationState,
    ExternalDelivery,
}

public sealed record IntentDeliverableRequirement(
    IntentDeliverableKind Kind,
    string Description,
    string? Format = null,
    string? Destination = null,
    string? ArtifactKey = null);

public sealed record IntentEntity(
    IntentEntityRole Role,
    string Type,
    string Value,
    string? ResolvedValue = null,
    double Confidence = 1d)
{
    public string EffectiveValue => string.IsNullOrWhiteSpace(ResolvedValue)
        ? Value
        : ResolvedValue;
}

public sealed record IntentSuccessCondition(
    IntentEvidenceKind Kind,
    string Description);

public sealed record IntentPlanStep(
    string Id,
    string Title,
    string Action,
    string? App,
    string? Target,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<IntentSuccessCondition> SuccessConditions,
    IntentRiskLevel Risk = IntentRiskLevel.Reversible,
    int EstimatedLatencyMilliseconds = 0);

/// <summary>
/// Keeps the five context classes separate so conversational history is never
/// mistaken for current desktop or execution state.
/// </summary>
public sealed record IntentContextSnapshot(
    IReadOnlyList<ConversationMessageSnapshot> Conversation,
    IReadOnlyDictionary<string, string> Desktop,
    IReadOnlyDictionary<string, string> Task,
    IReadOnlyDictionary<string, string> Application,
    IReadOnlyList<string> UserPreferences,
    IReadOnlyDictionary<string, string> RecentEntities)
{
    public static IntentContextSnapshot Empty { get; } = new(
        Array.Empty<ConversationMessageSnapshot>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        Array.Empty<string>(),
        new Dictionary<string, string>());
}

/// <summary>
/// The sole semantic hand-off from natural-language understanding to routing,
/// planning, and execution.  No consumer needs to inspect the original words.
/// </summary>
public sealed class IntentResolution
{
    public IntentResolution(
        SemanticTaskType taskType,
        IntentExecutionDomain domain,
        string goal,
        bool requiresComputer,
        IReadOnlyList<string>? actions = null,
        IReadOnlyList<string>? applications = null,
        IReadOnlyList<IntentEntity>? entities = null,
        IReadOnlyList<string>? constraints = null,
        IReadOnlyList<IntentSuccessCondition>? successConditions = null,
        IReadOnlyList<IntentPlanStep>? plan = null,
        IntentRiskLevel risk = IntentRiskLevel.None,
        bool requiresClarification = false,
        string? clarificationReason = null,
        string? clarificationQuestion = null,
        double confidence = 0d,
        string source = "semantic-engine",
        long elapsedMilliseconds = 0,
        IReadOnlyList<IntentDeliverableRequirement>? deliverables = null)
    {
        if (!Enum.IsDefined(taskType)) throw new ArgumentOutOfRangeException(nameof(taskType));
        if (!Enum.IsDefined(domain)) throw new ArgumentOutOfRangeException(nameof(domain));
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("A semantic goal is required.", nameof(goal));
        }

        TaskType = taskType;
        Domain = domain;
        Goal = goal.Trim();
        // A semantic answer contract is the authoritative boundary for a
        // conversation-only turn. Small local models can occasionally emit a
        // contradictory requires_computer=true bit while simultaneously
        // selecting Answer + a conversational deliverable. Normalize the
        // structure itself instead of matching any wording from the user.
        RequiresComputer = taskType == SemanticTaskType.Answer
            ? false
            : requiresComputer;
        Actions = NormalizeText(actions);
        Applications = NormalizeText(applications);
        Entities = CopyEntities(entities);
        Constraints = NormalizeText(constraints);
        SuccessConditions = CopyConditions(successConditions);
        Deliverables = CopyDeliverables(deliverables);
        Plan = CopyPlan(plan);
        Risk = risk;
        RequiresClarification = requiresClarification || taskType == SemanticTaskType.Clarify;
        ClarificationReason = NormalizeOptional(clarificationReason);
        ClarificationQuestion = NormalizeOptional(clarificationQuestion);
        Confidence = Math.Clamp(confidence, 0d, 1d);
        Source = string.IsNullOrWhiteSpace(source) ? "semantic-engine" : source.Trim();
        ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds);
    }

    public SemanticTaskType TaskType { get; }

    public IntentExecutionDomain Domain { get; }

    public string Goal { get; }

    public bool RequiresComputer { get; }

    public IReadOnlyList<string> Actions { get; }

    public IReadOnlyList<string> Applications { get; }

    public IReadOnlyList<IntentEntity> Entities { get; }

    public IReadOnlyList<string> Constraints { get; }

    public IReadOnlyList<IntentSuccessCondition> SuccessConditions { get; }

    public IReadOnlyList<IntentDeliverableRequirement> Deliverables { get; }

    public IReadOnlyList<IntentPlanStep> Plan { get; }

    public IntentRiskLevel Risk { get; }

    public bool RequiresClarification { get; }

    public string? ClarificationReason { get; }

    public string? ClarificationQuestion { get; }

    public double Confidence { get; }

    public string Source { get; }

    public long ElapsedMilliseconds { get; }

    public bool IsConfident => Confidence >= 0.64;

    public SemanticIntentDecision ToLegacyDecision() => new(
        RequiresClarification
            ? SemanticIntentMode.Clarify
            : TaskType switch
            {
                SemanticTaskType.Answer => SemanticIntentMode.Answer,
                SemanticTaskType.Create when !RequiresComputer => SemanticIntentMode.Answer,
                SemanticTaskType.Hybrid or SemanticTaskType.MultiAppTask =>
                    SemanticIntentMode.Mixed,
                _ => RequiresComputer
                    ? SemanticIntentMode.Operate
                    : SemanticIntentMode.Answer,
            },
        Domain switch
        {
            IntentExecutionDomain.Communication => SemanticIntentDomain.Chat,
            IntentExecutionDomain.File => SemanticIntentDomain.File,
            IntentExecutionDomain.Office => SemanticIntentDomain.Office,
            IntentExecutionDomain.Browser => SemanticIntentDomain.Browser,
            IntentExecutionDomain.Media => SemanticIntentDomain.Media,
            IntentExecutionDomain.System => SemanticIntentDomain.System,
            IntentExecutionDomain.Research => SemanticIntentDomain.Research,
            IntentExecutionDomain.Development => SemanticIntentDomain.Application,
            IntentExecutionDomain.MultiApp => SemanticIntentDomain.Other,
            _ => SemanticIntentDomain.Other,
        },
        Confidence,
        ElapsedMilliseconds,
        Source);

    public AgentTaskRoute ToAgentRoute()
    {
        if (RequiresClarification || TaskType is SemanticTaskType.Answer or SemanticTaskType.Clarify)
        {
            return AgentTaskRoute.DirectAnswer;
        }

        if (TaskType == SemanticTaskType.MultiAppTask ||
            Domain == IntentExecutionDomain.MultiApp ||
            Applications.Count > 1)
        {
            return AgentTaskRoute.Composite;
        }

        if (TaskType == SemanticTaskType.Hybrid && RequiresComputer)
        {
            return AgentTaskRoute.Composite;
        }

        return Domain switch
        {
            IntentExecutionDomain.Research when !RequiresComputer => AgentTaskRoute.Research,
            IntentExecutionDomain.Office when IsPresentationIntent() =>
                AgentTaskRoute.Presentation,
            IntentExecutionDomain.File or IntentExecutionDomain.Office => AgentTaskRoute.FileWork,
            IntentExecutionDomain.Answer => AgentTaskRoute.DirectAnswer,
            _ => RequiresComputer
                ? AgentTaskRoute.ComputerControl
                : AgentTaskRoute.DirectAnswer,
        };
    }

    public ComputerTaskContract ToComputerTaskContract()
    {
        var route = ToAgentRoute();
        if (route == AgentTaskRoute.DirectAnswer)
        {
            throw new InvalidOperationException("An answer-only intent has no computer-task contract.");
        }

        var mode = TaskType == SemanticTaskType.Hybrid ||
                   TaskType == SemanticTaskType.MultiAppTask
            ? ComputerTaskMode.Mixed
            : ComputerTaskMode.Operate;
        if (TaskType == SemanticTaskType.ComputerTask &&
            Actions.Any(action => action.Equals("monitor", StringComparison.OrdinalIgnoreCase)))
        {
            mode = ComputerTaskMode.Monitor;
        }

        var operation = Actions.FirstOrDefault() ?? route switch
        {
            AgentTaskRoute.Research => ComputerTaskOperations.Research,
            AgentTaskRoute.FileWork => ComputerTaskOperations.FileWork,
            AgentTaskRoute.Presentation => ComputerTaskOperations.Presentation,
            AgentTaskRoute.Composite => ComputerTaskOperations.Composite,
            _ => ComputerTaskOperations.ComputerControl,
        };
        var app = Applications.FirstOrDefault() ?? FindEntity(IntentEntityRole.Application);
        var target = FindEntity(IntentEntityRole.Recipient) ??
                     FindEntity(IntentEntityRole.Target) ??
                     FindEntity(IntentEntityRole.Object) ??
                     FindEntity(IntentEntityRole.File);
        var evidence = SuccessConditions.Count > 0
            ? SuccessConditions.Select(ToContractEvidence).ToArray()
            : Deliverables.Count > 0
                ? Deliverables.Select(ToContractEvidence).ToArray()
                : new[]
                {
                    new CompletionEvidenceRequirement(
                        CompletionEvidenceKind.StateObservation,
                        "The semantic success condition is observed."),
                };
        var ambiguity = RequiresClarification
            ? ComputerTaskAmbiguity.Unresolved(
                ClarificationReason ?? "A required task entity is unresolved.",
                clarificationQuestion: ClarificationQuestion)
            : ComputerTaskAmbiguity.None;

        return new ComputerTaskContract(
            mode,
            Goal,
            operation,
            target,
            app,
            source: "semantic-intent",
            constraints: Constraints,
            completionEvidence: evidence,
            risk: Risk switch
            {
                IntentRiskLevel.None => ComputerTaskRisk.None,
                IntentRiskLevel.Reversible => ComputerTaskRisk.Low,
                IntentRiskLevel.ExternalEffect => ComputerTaskRisk.Medium,
                IntentRiskLevel.HighImpact => ComputerTaskRisk.High,
                _ => ComputerTaskRisk.Medium,
            },
            ambiguity: ambiguity);
    }

    public string FormatForAgent() => JsonSerializer.Serialize(
        new
        {
            task_type = ToAgentValue(TaskType),
            domain = ToAgentValue(Domain),
            goal = Goal,
            requires_computer = RequiresComputer,
            actions = Actions,
            applications = Applications,
            entities = Entities.Select(entity => new
            {
                role = ToAgentValue(entity.Role),
                type = entity.Type,
                value = entity.Value,
                resolved_value = entity.ResolvedValue,
                confidence = entity.Confidence,
            }),
            constraints = Constraints,
            success_conditions = SuccessConditions.Select(condition => new
            {
                kind = ToAgentValue(condition.Kind),
                description = condition.Description,
            }),
            deliverables = Deliverables.Select(deliverable => new
            {
                kind = ToAgentValue(deliverable.Kind),
                description = deliverable.Description,
                format = deliverable.Format,
                destination = deliverable.Destination,
            }),
            risk = ToAgentValue(Risk),
            clarification = new
            {
                required = RequiresClarification,
                reason = ClarificationReason,
                question = ClarificationQuestion,
            },
            confidence = Confidence,
            source = Source,
        },
        JsonOptions);

    private bool IsPresentationIntent()
    {
        static string Normalize(string value) => value
            .Trim()
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToLowerInvariant();

        return Applications.Select(Normalize).Any(value => value is
                   "powerpoint" or "presentation" or "slide_deck" or "slides" or "ppt") ||
               Actions.Select(Normalize).Any(value => value is
                   "create_presentation" or "edit_presentation" or
                   "build_presentation" or "export_presentation" or
                   "create_slides" or "edit_slides");
    }

    private string? FindEntity(IntentEntityRole role) => Entities
        .Where(entity => entity.Role == role)
        .OrderByDescending(entity => entity.Confidence)
        .Select(entity => entity.EffectiveValue)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static CompletionEvidenceRequirement ToContractEvidence(
        IntentSuccessCondition condition) => new(
        condition.Kind switch
        {
            IntentEvidenceKind.ReturnValue => CompletionEvidenceKind.ReturnValue,
            IntentEvidenceKind.Artifact => CompletionEvidenceKind.Artifact,
            IntentEvidenceKind.UserConfirmation => CompletionEvidenceKind.UserConfirmation,
            _ => CompletionEvidenceKind.StateObservation,
        },
        condition.Description);

    private static CompletionEvidenceRequirement ToContractEvidence(
        IntentDeliverableRequirement deliverable) => new(
        deliverable.Kind switch
        {
            IntentDeliverableKind.ConversationalAnswer or
            IntentDeliverableKind.ResearchResult => CompletionEvidenceKind.ReturnValue,
            IntentDeliverableKind.Document or
            IntentDeliverableKind.Spreadsheet or
            IntentDeliverableKind.Presentation or
            IntentDeliverableKind.Image or
            IntentDeliverableKind.Audio or
            IntentDeliverableKind.Video or
            IntentDeliverableKind.File or
            IntentDeliverableKind.ModifiedFile => CompletionEvidenceKind.Artifact,
            IntentDeliverableKind.ExternalDelivery => CompletionEvidenceKind.StateObservation,
            _ => CompletionEvidenceKind.StateObservation,
        },
        deliverable.Description);

    private static IReadOnlyList<string> NormalizeText(IReadOnlyList<string>? values) =>
        values is not { Count: > 0 }
            ? Array.Empty<string>()
            : Array.AsReadOnly(values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static IReadOnlyList<IntentEntity> CopyEntities(
        IReadOnlyList<IntentEntity>? entities) => entities is not { Count: > 0 }
        ? Array.Empty<IntentEntity>()
        : Array.AsReadOnly(entities
            .Where(entity => entity is not null &&
                             !string.IsNullOrWhiteSpace(entity.Type) &&
                             !string.IsNullOrWhiteSpace(entity.Value))
            .Select(entity => entity with
            {
                Type = entity.Type.Trim(),
                Value = entity.Value.Trim(),
                ResolvedValue = NormalizeOptional(entity.ResolvedValue),
                Confidence = Math.Clamp(entity.Confidence, 0d, 1d),
            })
            .ToArray());

    private static IReadOnlyList<IntentSuccessCondition> CopyConditions(
        IReadOnlyList<IntentSuccessCondition>? conditions) => conditions is not { Count: > 0 }
        ? Array.Empty<IntentSuccessCondition>()
        : Array.AsReadOnly(conditions
            .Where(condition => condition is not null &&
                                !string.IsNullOrWhiteSpace(condition.Description))
            .Select(condition => condition with
            {
                Description = condition.Description.Trim(),
            })
            .ToArray());

    private static IReadOnlyList<IntentDeliverableRequirement> CopyDeliverables(
        IReadOnlyList<IntentDeliverableRequirement>? deliverables) =>
        deliverables is not { Count: > 0 }
            ? Array.Empty<IntentDeliverableRequirement>()
            : Array.AsReadOnly(deliverables
                .Where(deliverable => deliverable is not null &&
                                      !string.IsNullOrWhiteSpace(deliverable.Description))
                .Select(deliverable => deliverable with
                {
                    Description = deliverable.Description.Trim(),
                    Format = NormalizeOptional(deliverable.Format),
                    Destination = NormalizeOptional(deliverable.Destination),
                })
                .Distinct()
                .ToArray());

    private static IReadOnlyList<IntentPlanStep> CopyPlan(
        IReadOnlyList<IntentPlanStep>? plan) => plan is not { Count: > 0 }
        ? Array.Empty<IntentPlanStep>()
        : Array.AsReadOnly(plan
            .Where(step => step is not null &&
                           !string.IsNullOrWhiteSpace(step.Id) &&
                           !string.IsNullOrWhiteSpace(step.Title) &&
                           !string.IsNullOrWhiteSpace(step.Action))
            .Select(step => step with
            {
                Id = step.Id.Trim(),
                Title = step.Title.Trim(),
                Action = step.Action.Trim(),
                App = NormalizeOptional(step.App),
                Target = NormalizeOptional(step.Target),
                DependsOn = NormalizeText(step.DependsOn),
                SuccessConditions = CopyConditions(step.SuccessConditions),
                EstimatedLatencyMilliseconds = Math.Max(0, step.EstimatedLatencyMilliseconds),
            })
            .ToArray());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToAgentValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
