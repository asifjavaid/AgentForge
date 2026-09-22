namespace AgentForge.Api.Contracts;

public sealed record AnalyzeRepositoryRequest(
    string? RepositoryPath,
    string? Request);
