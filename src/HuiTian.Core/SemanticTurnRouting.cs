namespace HuiTian.Core;

public sealed record SemanticTurnRoute(
    AgentTaskRoute Route,
    bool AllowProductFastPath,
    bool NeedsComputerContext);

/// <summary>
/// Selects the actual product entry path from a model decision, never from
/// user words. This is a routing envelope; the executor still declares the
/// full goal/deliverables and verifies them before marking work complete.
/// </summary>
public static class SemanticTurnRouting
{
    public static SemanticTurnRoute Decide(
        SemanticIntentDecision? decision,
        bool hasAttachments,
        bool isSteering)
    {
        if (isSteering || decision is not { IsConfident: true })
            return new(AgentTaskRoute.Composite, false, true);

        if (decision.Mode is SemanticIntentMode.Answer or SemanticIntentMode.Clarify)
        {
            // Attached files may require a real read before answering.
            return hasAttachments
                ? new(AgentTaskRoute.Composite, false, true)
                : new(AgentTaskRoute.DirectAnswer, false, false);
        }

        // Retain the verified universal executor for computer tasks, including
        // multiple deliveries. Only confirmed single-domain operations can
        // attempt deterministic adapters; mismatches fall through unchanged.
        return new(AgentTaskRoute.Composite,
            decision.Mode == SemanticIntentMode.Operate, true);
    }
}
