namespace AgentForge.Application.SoftwareAgents;

public sealed class SoftwareAgentOptions
{
    public const string SectionName = "SoftwareAgent";

    public string RepositoryPath { get; init; } = ".";
    public int MaxSteps { get; init; } = 8;
    public int MaxToolCalls { get; init; } = 12;
    public int MaxKnowledgeResults { get; init; } = 4;
    public int MaxTotalToolResultCharacters { get; init; } = 60_000;
    public int TimeoutSeconds { get; init; } = 180;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
            throw new InvalidOperationException("SoftwareAgent:RepositoryPath is required.");
        if (MaxSteps is < 1 or > 20)
            throw new InvalidOperationException("SoftwareAgent:MaxSteps must be between 1 and 20.");
        if (MaxToolCalls is < 1 or > 50)
            throw new InvalidOperationException("SoftwareAgent:MaxToolCalls must be between 1 and 50.");
        if (MaxKnowledgeResults is < 1 or > 10)
            throw new InvalidOperationException("SoftwareAgent:MaxKnowledgeResults must be between 1 and 10.");
        if (MaxTotalToolResultCharacters is < 1_024 or > 500_000)
            throw new InvalidOperationException("SoftwareAgent:MaxTotalToolResultCharacters is outside the permitted range.");
        if (TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("SoftwareAgent:TimeoutSeconds must be between 1 and 600.");
    }

    public TimeSpan GetTimeout() => TimeSpan.FromSeconds(TimeoutSeconds);
}
