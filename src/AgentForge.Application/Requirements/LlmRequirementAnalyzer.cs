using AgentForge.Application.Llm;
using AgentForge.Domain.Requirements;

namespace AgentForge.Application.Requirements;

public sealed class LlmRequirementAnalyzer(IStructuredOutputClient structuredOutputClient)
    : IRequirementAnalyzer
{
    public Task<RequirementAnalysis> AnalyzeAsync(
        string requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);

        var request = new StructuredOutputRequest(
            RequirementAnalysisPrompt.SystemMessage,
            requirement,
            RequirementAnalysisSchema.Name,
            RequirementAnalysisSchema.Json);

        return structuredOutputClient.GenerateAsync<RequirementAnalysis>(
            request,
            cancellationToken);
    }
}
