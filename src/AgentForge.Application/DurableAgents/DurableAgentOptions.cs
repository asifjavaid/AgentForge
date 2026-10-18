namespace AgentForge.Application.DurableAgents;

public sealed class DurableAgentOptions
{
    public const string SectionName = "DurableAgent";

    public string StoreProvider { get; init; } = "File";
    public string FilePath { get; init; } = "App_Data/agent-checkpoints";
    public int LeaseSeconds { get; init; } = 120;
    public int MaxCheckpointBytes { get; init; } = 524_288;
    public int MaxConversationMessages { get; init; } = 100;

    public void Validate()
    {
        if (StoreProvider is not ("File" or "AzureTable" or "InMemory"))
            throw new InvalidOperationException("DurableAgent:StoreProvider must be File, AzureTable, or InMemory.");
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("DurableAgent:FilePath is required.");
        if (LeaseSeconds is < 15 or > 900)
            throw new InvalidOperationException("DurableAgent:LeaseSeconds must be between 15 and 900.");
        if (MaxCheckpointBytes is < 16_384 or > 3_000_000)
            throw new InvalidOperationException("DurableAgent:MaxCheckpointBytes is outside the permitted range.");
        if (MaxConversationMessages is < 4 or > 500)
            throw new InvalidOperationException("DurableAgent:MaxConversationMessages is outside the permitted range.");
    }

    public TimeSpan GetLeaseDuration() => TimeSpan.FromSeconds(LeaseSeconds);
}
