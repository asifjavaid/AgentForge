namespace AgentForge.Application.Repositories;

public sealed class RepositoryAgentOptions
{
    public const string SectionName = "Agent";

    public int MaxIterations { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 120;

    public int GetMaxIterations() => Math.Clamp(MaxIterations, 1, 20);

    public TimeSpan GetTimeout() => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600));
}
