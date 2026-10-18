namespace AgentForge.Application.MultiAgent;

public static class WorkflowStateMachine
{
    public static bool CanTransition(WorkflowStatus from, WorkflowStatus to) => (from, to) switch
    {
        (WorkflowStatus.Created, WorkflowStatus.Planning) => true,
        (WorkflowStatus.Created, WorkflowStatus.Cancelled) => true,
        (WorkflowStatus.Planning, WorkflowStatus.Running) => true,
        (WorkflowStatus.Planning, WorkflowStatus.WaitingToResume) => true,
        (WorkflowStatus.Planning, WorkflowStatus.Failed) => true,
        (WorkflowStatus.Planning, WorkflowStatus.Cancelled) => true,
        (WorkflowStatus.Running, WorkflowStatus.WaitingToResume) => true,
        (WorkflowStatus.Running, WorkflowStatus.Completed) => true,
        (WorkflowStatus.Running, WorkflowStatus.CompletedWithConcerns) => true,
        (WorkflowStatus.Running, WorkflowStatus.InsufficientEvidence) => true,
        (WorkflowStatus.Running, WorkflowStatus.Failed) => true,
        (WorkflowStatus.Running, WorkflowStatus.Cancelled) => true,
        (WorkflowStatus.WaitingToResume, WorkflowStatus.Planning) => true,
        (WorkflowStatus.WaitingToResume, WorkflowStatus.Running) => true,
        (WorkflowStatus.WaitingToResume, WorkflowStatus.Cancelled) => true,
        _ => false
    };

    public static void EnsureTransition(WorkflowStatus from, WorkflowStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Workflow transition {from} -> {to} is not permitted.");
    }
}
