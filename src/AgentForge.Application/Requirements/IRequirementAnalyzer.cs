using AgentForge.Domain.Requirements;

namespace AgentForge.Application.Requirements;

public interface IRequirementAnalyzer
{
    Task<RequirementAnalysis> AnalyzeAsync(
        string requirement,
        CancellationToken cancellationToken = default);
}
