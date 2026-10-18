namespace AgentForge.Application.MultiAgent;

public sealed record class MultiAgentWorkflowOptions
{
    public const string SectionName = "MultiAgentWorkflow";

    public string StoreProvider { get; init; } = "File";
    public string FilePath { get; init; } = "App_Data/workflow-checkpoints";
    public int LeaseSeconds { get; init; } = 300;
    public int MaxPlannedTasks { get; init; } = 8;
    public int MaxDependencyDepth { get; init; } = 6;
    public int MaxConcurrency { get; init; } = 2;
    public int MaxModelCallsPerSpecialist { get; init; } = 5;
    public int MaxTotalModelCalls { get; init; } = 24;
    public int MaxTotalToolCalls { get; init; } = 24;
    public int MaxTotalTokens { get; init; } = 100_000;
    public int MaxTaskOutputCharacters { get; init; } = 40_000;
    public int MaxCheckpointBytes { get; init; } = 2_000_000;
    public int MaxEvents { get; init; } = 200;
    public int MaxRevisionCycles { get; init; } = 2;
    public int MaxTaskAttempts { get; init; } = 8;
    public int AttemptTimeoutSeconds { get; init; } = 300;

    public void Validate()
    {
        if (StoreProvider is not ("File" or "InMemory"))
            throw new InvalidOperationException("MultiAgentWorkflow:StoreProvider must be File or InMemory.");
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("MultiAgentWorkflow:FilePath is required.");
        if (LeaseSeconds is < 15 or > 900)
            throw new InvalidOperationException("MultiAgentWorkflow:LeaseSeconds must be between 15 and 900.");
        if (MaxPlannedTasks is < 1 or > 20 || MaxDependencyDepth is < 1 or > 12)
            throw new InvalidOperationException("Multi-agent plan bounds are invalid.");
        if (MaxConcurrency is < 1 or > 8 || MaxModelCallsPerSpecialist is < 1 or > 10)
            throw new InvalidOperationException("Multi-agent execution bounds are invalid.");
        if (MaxTotalModelCalls is < 1 or > 100 || MaxTotalToolCalls is < 0 or > 200)
            throw new InvalidOperationException("Multi-agent call budgets are invalid.");
        if (MaxTotalTokens is < 1_000 or > 2_000_000)
            throw new InvalidOperationException("Multi-agent token budget is invalid.");
        if (MaxTaskOutputCharacters is < 1_024 or > 200_000 || MaxCheckpointBytes is < 16_384 or > 4_000_000)
            throw new InvalidOperationException("Multi-agent storage bounds are invalid.");
        if (MaxEvents is < 10 or > 2_000 || MaxRevisionCycles is < 0 or > 5)
            throw new InvalidOperationException("Multi-agent history bounds are invalid.");
        if (MaxTaskAttempts is < 1 or > 20)
            throw new InvalidOperationException("Multi-agent task attempt bounds are invalid.");
        if (AttemptTimeoutSeconds is < 10 or > 1_800)
            throw new InvalidOperationException("Multi-agent attempt timeout is invalid.");
    }

    public TimeSpan GetLeaseDuration() => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan GetAttemptTimeout() => TimeSpan.FromSeconds(AttemptTimeoutSeconds);
}
