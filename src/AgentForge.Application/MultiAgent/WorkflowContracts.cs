namespace AgentForge.Application.MultiAgent;

public interface IWorkflowCheckpointStore
{
    Task<MultiAgentWorkflowCheckpoint?> GetAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<MultiAgentWorkflowCheckpoint> CreateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<WorkflowCheckpointWriteResult> TryUpdateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowPlanner
{
    Task<WorkflowPlan> CreatePlanAsync(
        string objective,
        CancellationToken cancellationToken = default);
}

public interface ISpecialistAgent
{
    AgentRole Role { get; }

    Task<AgentTaskExecutionResult> ExecuteAsync(
        WorkflowTaskState task,
        AgentExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IMultiAgentWorkflowManager
{
    Task<MultiAgentWorkflowView> CreateAsync(
        string objective,
        CancellationToken cancellationToken = default);

    Task<MultiAgentWorkflowView> GetAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<MultiAgentWorkflowView> ResumeAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<MultiAgentWorkflowView> CancelAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<string> GetReportAsync(
        string workflowId,
        CancellationToken cancellationToken = default);
}
