using System.Diagnostics;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;
using Azure;
using Microsoft.Extensions.Logging;

namespace AgentForge.Infrastructure.AzureSearch;

internal sealed class AzureAiSearchIndexManager(
    AzureAiSearchOptions options,
    RagOptions ragOptions,
    IKnowledgeDocumentLoader loader,
    IKnowledgeChunker chunker,
    IEmbeddingClient embeddingClient,
    IAzureSearchGateway gateway,
    AzureSearchIndexState state,
    ILogger<AzureAiSearchIndexManager> logger) : IKnowledgeIndexManager
{
    public async Task<KnowledgeIndexingResult> InitializeAndIndexAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var timer = Stopwatch.StartNew();
        var indexName = options.GetIndexName();
        var created = false;

        try
        {
            var existing = await gateway.GetIndexAsync(cancellationToken);
            if (existing is null)
            {
                await gateway.CreateIndexAsync(AzureSearchIndexDefinition.Create(indexName), cancellationToken);
                created = true;
            }
            else
            {
                AzureSearchIndexDefinition.Validate(existing);
            }
        }
        catch (RequestFailedException exception) when (exception.Status is 401 or 403)
        {
            throw new AzureSearchAccessException(
                "index schema management", "Search Service Contributor", exception);
        }

        var documents = await loader.LoadAsync(cancellationToken);
        var chunks = documents.SelectMany(chunker.Chunk).ToArray();
        var embedded = new List<KnowledgeChunk>(chunks.Length);
        var calls = 0;
        long embeddingDuration = 0;
        var embeddingTokens = 0;
        var hasEmbeddingTokens = false;

        foreach (var batch in chunks.Chunk(ragOptions.EmbeddingBatchSize))
        {
            var response = await embeddingClient.EmbedBatchAsync(batch.Select(item => item.Text).ToArray(), cancellationToken);
            calls++;
            embeddingDuration += response.DurationMilliseconds;
            if (response.InputTokens is int tokens)
            {
                embeddingTokens += tokens;
                hasEmbeddingTokens = true;
            }
            if (response.Embeddings.Count != batch.Length)
                throw new InvalidOperationException("Embedding count did not match chunk count.");
            for (var index = 0; index < batch.Length; index++)
            {
                if (response.Embeddings[index].Values.Count != AzureSearchIndexDefinition.VectorDimensions)
                    throw new InvalidOperationException("Embedding dimensions do not match the Azure AI Search index schema.");
                embedded.Add(batch[index] with { Embedding = response.Embeddings[index].Values });
            }
        }

        AzureSearchUploadResult upload;
        try
        {
            upload = await gateway.UpsertAsync(
                embedded.Select(AzureSearchKnowledgeDocument.FromChunk).ToArray(), cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 401 or 403)
        {
            throw new AzureSearchAccessException(
                "document upsert", "Search Index Data Contributor", exception);
        }
        var failures = upload.Errors;
        var upserted = upload.Uploaded;
        timer.Stop();

        state.Diagnostics = new KnowledgeIndexDiagnostics(
            documents.Count, chunks.Length, calls, AzureSearchIndexDefinition.VectorDimensions,
            embeddingDuration, hasEmbeddingTokens ? embeddingTokens : null,
            timer.ElapsedMilliseconds, failures);

        logger.LogInformation(
            "Azure AI Search indexing completed. Index: {IndexName}; Created: {Created}; Documents: {Documents}; Chunks: {Chunks}; Upserted: {Upserted}; EmbeddingCalls: {EmbeddingCalls}; DurationMs: {DurationMs}; Errors: {Errors}",
            indexName, created, documents.Count, chunks.Length, upserted, calls, timer.ElapsedMilliseconds, failures.Count);

        return new KnowledgeIndexingResult(
            created, true, documents.Count, chunks.Length, upserted, calls,
            embeddingDuration, timer.ElapsedMilliseconds, failures);
    }
}
