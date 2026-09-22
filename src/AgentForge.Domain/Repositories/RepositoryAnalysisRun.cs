namespace AgentForge.Domain.Repositories;

public sealed record RepositoryAnalysisRun(
    RepositoryAnalysis Analysis,
    RepositoryAnalysisMetadata Metadata);

public sealed record RepositoryAnalysisMetadata(
    string Provider,
    string Model,
    int Iterations,
    IReadOnlyList<string> ToolCalls,
    long DurationMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);
