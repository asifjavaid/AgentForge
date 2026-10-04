using AgentForge.Application.Knowledge;
using AgentForge.Infrastructure.Knowledge;
using AgentForge.Infrastructure.AzureSearch;

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
            EmbeddingBatchSize = configured.EmbeddingBatchSize,
            Retriever = configured.Retriever
        };
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IKnowledgeDocumentLoader, MarkdownKnowledgeDocumentLoader>();
        services.AddSingleton<IKnowledgeChunker, DeterministicTextChunker>();
        services.AddSingleton<IKnowledgeVectorStore, InMemoryKnowledgeVectorStore>();
        if (options.Retriever == KnowledgeRetrieverKind.InMemory)
        {
            services.AddSingleton<IKnowledgeRetriever, InMemoryKnowledgeRetriever>();
        }
        else
        {
            var searchOptions = configuration.GetSection(AzureAiSearchOptions.SectionName).Get<AzureAiSearchOptions>()
                ?? new AzureAiSearchOptions();
            services.AddAzureAiSearchKnowledge(searchOptions);
        }
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        return services;
    }
}
