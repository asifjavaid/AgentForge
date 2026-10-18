namespace AgentForge.Application.DurableAgents;

public static class DurableAgentStateMachine
{
    public static bool CanTransition(DurableAgentRunStatus from, DurableAgentRunStatus to) =>
        (from, to) switch
        {
            (DurableAgentRunStatus.Created, DurableAgentRunStatus.Running) => true,
            (DurableAgentRunStatus.Created, DurableAgentRunStatus.Cancelled) => true,
            (DurableAgentRunStatus.Running, DurableAgentRunStatus.WaitingToResume) => true,
            (DurableAgentRunStatus.Running, DurableAgentRunStatus.Completed) => true,
            (DurableAgentRunStatus.Running, DurableAgentRunStatus.InsufficientEvidence) => true,
            (DurableAgentRunStatus.Running, DurableAgentRunStatus.Failed) => true,
            (DurableAgentRunStatus.Running, DurableAgentRunStatus.Cancelled) => true,
            (DurableAgentRunStatus.WaitingToResume, DurableAgentRunStatus.Running) => true,
            (DurableAgentRunStatus.WaitingToResume, DurableAgentRunStatus.Cancelled) => true,
            _ => false
        };

    public static void EnsureTransition(DurableAgentRunStatus from, DurableAgentRunStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Durable agent transition {from} -> {to} is not permitted.");
    }
}
