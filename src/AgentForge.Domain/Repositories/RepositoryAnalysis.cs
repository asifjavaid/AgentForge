namespace AgentForge.Domain.Repositories;

public sealed record RepositoryAnalysis(
    string Summary,
    IReadOnlyList<string> ArchitectureComponents,
    IReadOnlyList<string> ImportantFiles,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> SecurityConcerns,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> UnansweredQuestions);
