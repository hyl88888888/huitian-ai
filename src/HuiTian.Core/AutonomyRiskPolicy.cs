namespace HuiTian.Core;

/// <summary>
/// Controls how much bounded computer work may run before asking the user.
/// </summary>
public enum AutonomyMode
{
    Cautious,
    Standard,
    HighAutonomy,
}

/// <summary>
/// The execution gate selected by <see cref="AutonomyRiskPolicy"/>.
/// </summary>
public enum AutonomyDecisionKind
{
    Proceed,
    RequireApproval,
    RequireClarification,
    Block,
}

/// <summary>
/// A data-only decision that can be consumed by any UI or executor.
/// </summary>
public sealed record AutonomyDecision(
    AutonomyMode Mode,
    ComputerTaskRisk Risk,
    AutonomyDecisionKind Kind,
    string Reason)
{
    public bool CanExecuteImmediately => Kind == AutonomyDecisionKind.Proceed;

    public bool RequiresUserInput =>
        Kind is AutonomyDecisionKind.RequireApproval or
            AutonomyDecisionKind.RequireClarification;
}

/// <summary>
/// Applies a monotonic autonomy policy to computer-task risk. More autonomous
/// modes may execute a strict superset of lower-risk work, while high-impact
/// work still requires approval and critical work is never auto-approved.
/// </summary>
public static class AutonomyRiskPolicy
{
    public static AutonomyDecision Evaluate(
        AutonomyMode mode,
        ComputerTaskRisk risk)
    {
        ValidateMode(mode);
        ValidateRisk(risk);

        if (risk == ComputerTaskRisk.Critical)
        {
            return Decision(
                mode,
                risk,
                AutonomyDecisionKind.Block,
                "Critical-risk work is blocked in every autonomy mode.");
        }

        var immediateExecutionLimit = mode switch
        {
            AutonomyMode.Cautious => ComputerTaskRisk.None,
            AutonomyMode.Standard => ComputerTaskRisk.Low,
            AutonomyMode.HighAutonomy => ComputerTaskRisk.Medium,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        if (risk <= immediateExecutionLimit)
        {
            return Decision(
                mode,
                risk,
                AutonomyDecisionKind.Proceed,
                $"{risk}-risk work is within the {mode} immediate-execution limit.");
        }

        return Decision(
            mode,
            risk,
            AutonomyDecisionKind.RequireApproval,
            $"{risk}-risk work requires approval in {mode} mode.");
    }

    /// <summary>
    /// Evaluates contract readiness before applying the risk matrix. An
    /// autonomy setting can never bypass ambiguity or an invalid task bound.
    /// </summary>
    public static AutonomyDecision Evaluate(
        AutonomyMode mode,
        ComputerTaskContract task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateMode(mode);

        if (task.Ambiguity.RequiresClarification)
        {
            return Decision(
                mode,
                task.Risk,
                AutonomyDecisionKind.RequireClarification,
                task.Ambiguity.ClarificationQuestion ??
                    task.Ambiguity.Reason ??
                    "The task requires clarification.");
        }

        var blockers = task.GetExecutionBlockers();
        if (blockers.Count > 0)
        {
            return Decision(
                mode,
                task.Risk,
                AutonomyDecisionKind.Block,
                blockers[0]);
        }

        return Evaluate(mode, task.Risk);
    }

    private static AutonomyDecision Decision(
        AutonomyMode mode,
        ComputerTaskRisk risk,
        AutonomyDecisionKind kind,
        string reason) => new(mode, risk, kind, reason);

    private static void ValidateMode(AutonomyMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void ValidateRisk(ComputerTaskRisk risk)
    {
        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }
    }
}
