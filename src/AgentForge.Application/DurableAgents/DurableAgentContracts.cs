namespace AgentForge.Application.DurableAgents;

public interface IAgentCheckpointStore
{
    Task<DurableAgentCheckpoint?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<DurableAgentCheckpoint> CreateAsync(
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<CheckpointWriteResult> TryUpdateAsync(
        DurableAgentCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IDurableAgentRunManager
{
    Task<DurableAgentRunView> CreateAsync(
        string question,
        CancellationToken cancellationToken = default);

    Task<DurableAgentRunView> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<DurableAgentRunView> ResumeAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<DurableAgentRunView> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
