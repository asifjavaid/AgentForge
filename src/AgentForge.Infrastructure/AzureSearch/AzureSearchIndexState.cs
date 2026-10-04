using AgentForge.Domain.Knowledge;

namespace AgentForge.Infrastructure.AzureSearch;

internal sealed class AzureSearchIndexState
{
    public KnowledgeIndexDiagnostics Diagnostics { get; set; } =
        new(0, 0, 0, AzureSearchIndexDefinition.VectorDimensions, 0, null, 0, []);
}
