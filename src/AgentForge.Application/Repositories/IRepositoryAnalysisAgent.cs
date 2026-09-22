using AgentForge.Domain.Repositories;

namespace AgentForge.Application.Repositories;

public interface IRepositoryAnalysisAgent
{
    Task<RepositoryAnalysisRun> AnalyzeAsync(
        string repositoryPath,
        string request,
        CancellationToken cancellationToken = default);
}
