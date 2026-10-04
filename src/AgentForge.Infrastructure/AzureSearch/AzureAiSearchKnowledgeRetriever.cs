using System.Diagnostics;
using AgentForge.Application.Knowledge;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace AgentForge.Infrastructure.AzureSearch;

internal sealed class AzureAiSearchKnowledgeRetriever(
    AzureAiSearchOptions options,
    RagOptions ragOptions,
    IEmbeddingClient embeddingClient,
    IAzureSearchGateway gateway,
    AzureSearchIndexState state,
    ILogger<AzureAiSearchKnowledgeRetriever> logger) : IKnowledgeRetriever
{
    public async Task<KnowledgeRetrieverResult> RetrieveAsync(
        KnowledgeRetrieverRequest request,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var mode = options.RetrievalMode;
        var total = Stopwatch.StartNew();
        var queryEmbeddingMilliseconds = 0L;
        IReadOnlyList<float>? queryVector = null;

        if (mode is KnowledgeRetrievalMode.Vector or KnowledgeRetrievalMode.Hybrid or KnowledgeRetrievalMode.SemanticHybrid)
        {
            var embedding = await embeddingClient.EmbedAsync(request.Question, cancellationToken);
            queryEmbeddingMilliseconds = embedding.DurationMilliseconds;
            queryVector = embedding.Embeddings.Single().Values;
        }

        var searchOptions = BuildSearchOptions(mode, options.TopK, queryVector, request.Source);
        var searchText = mode == KnowledgeRetrievalMode.Vector ? null : request.Question;
        var searchTimer = Stopwatch.StartNew();
        List<AzureSearchQueryResult> raw;
        try
        {
            raw = (await gateway.SearchAsync(searchText, searchOptions, cancellationToken)).ToList();
        }
        catch (RequestFailedException exception) when (exception.Status is 401 or 403)
        {
            throw new AzureSearchAccessException(
                "document query", "Search Index Data Reader (or Search Index Data Contributor)", exception);
        }
        searchTimer.Stop();

        if (mode == KnowledgeRetrievalMode.Vector)
        {
            var minimumScore = ScoreFromCosine(ragOptions.MinimumSimilarity);
            raw.RemoveAll(item => item.Score < minimumScore);
        }

        var matches = raw.Take(options.TopK).Select((item, offset) => new KnowledgeRetrieverMatch(
            item.Document.ToChunk(), offset + 1, mode, item.Score, null,
            item.RerankerScore)).ToArray();
        total.Stop();

        logger.LogInformation(
            "Azure AI Search retrieval completed. Mode: {Mode}; TopK: {TopK}; Results: {Results}; QueryEmbeddingMs: {QueryEmbeddingMs}; SearchMs: {SearchMs}; DurationMs: {DurationMs}",
            mode, options.TopK, matches.Length, queryEmbeddingMilliseconds,
            searchTimer.ElapsedMilliseconds, total.ElapsedMilliseconds);

        return new KnowledgeRetrieverResult(
            matches, mode, queryEmbeddingMilliseconds, searchTimer.ElapsedMilliseconds,
            total.ElapsedMilliseconds, state.Diagnostics);
    }

    internal static SearchOptions BuildSearchOptions(
        KnowledgeRetrievalMode mode,
        int topK,
        IReadOnlyList<float>? queryVector,
        string? source)
    {
        if (mode == KnowledgeRetrievalMode.InMemoryVector)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if ((mode is KnowledgeRetrievalMode.Vector or KnowledgeRetrievalMode.Hybrid or KnowledgeRetrievalMode.SemanticHybrid) && queryVector is null)
            throw new ArgumentNullException(nameof(queryVector));

        var searchOptions = new SearchOptions { Size = topK };
        foreach (var field in new[] { "documentId", "source", "chunkId", "position", "content" })
            searchOptions.Select.Add(field);
        if (source is not null)
            searchOptions.Filter = SearchFilter.Create($"source eq {source}");

        if (mode is KnowledgeRetrievalMode.Vector or KnowledgeRetrievalMode.Hybrid or KnowledgeRetrievalMode.SemanticHybrid)
        {
            var vector = new VectorizedQuery(queryVector!.ToArray()) { KNearestNeighborsCount = topK };
            vector.Fields.Add(AzureSearchIndexDefinition.VectorField);
            searchOptions.VectorSearch = new VectorSearchOptions();
            searchOptions.VectorSearch.Queries.Add(vector);
        }

        if (mode == KnowledgeRetrievalMode.SemanticHybrid)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = AzureSearchIndexDefinition.SemanticConfiguration
            };
        }

        return searchOptions;
    }

    internal static double ScoreFromCosine(double cosine) => 1d / (2d - cosine);
}
