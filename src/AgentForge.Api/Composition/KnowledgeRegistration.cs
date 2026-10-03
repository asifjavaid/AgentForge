using AgentForge.Application.Knowledge;
using AgentForge.Infrastructure.Knowledge;

namespace AgentForge.Api.Composition;

public static class KnowledgeRegistration
{
    public static IServiceCollection AddKnowledge(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration.GetSection(RagOptions.SectionName).Get<RagOptions>() ?? new RagOptions();
        var options = new RagOptions
        {
            CorpusPath = Path.IsPathRooted(configured.CorpusPath)
                ? configured.CorpusPath
                : Path.Combine(environment.ContentRootPath, configured.CorpusPath),
            ChunkSize = configured.ChunkSize,
            ChunkOverlap = configured.ChunkOverlap,
            TopK = configured.TopK,
            MinimumSimilarity = configured.MinimumSimilarity,
            MaxDocumentBytes = configured.MaxDocumentBytes,
            EmbeddingBatchSize = configured.EmbeddingBatchSize
        };
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IKnowledgeDocumentLoader, MarkdownKnowledgeDocumentLoader>();
        services.AddSingleton<IKnowledgeChunker, DeterministicTextChunker>();
        services.AddSingleton<IKnowledgeVectorStore, InMemoryKnowledgeVectorStore>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        return services;
    }
}
