using System.Diagnostics;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed class InMemoryKnowledgeRetriever(
    IKnowledgeDocumentLoader loader,
    IKnowledgeChunker chunker,
    IEmbeddingClient embeddingClient,
    IKnowledgeVectorStore vectorStore,
    RagOptions options) : IKnowledgeRetriever
{
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private KnowledgeIndexDiagnostics? _index;

    public async Task<KnowledgeRetrieverResult> RetrieveAsync(
        KnowledgeRetrieverRequest request,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var index = await EnsureIndexedAsync(cancellationToken);
        var queryResult = await embeddingClient.EmbedAsync(request.Question, cancellationToken);
        var search = Stopwatch.StartNew();
        var matches = vectorStore.Search(
            queryResult.Embeddings.Single().Values,
            options.TopK,
            options.MinimumSimilarity,
            request.Source);
        search.Stop();
        total.Stop();

        return new KnowledgeRetrieverResult(
            matches.Select((match, offset) => new KnowledgeRetrieverMatch(
                match.Chunk, offset + 1, KnowledgeRetrievalMode.InMemoryVector,
                match.Similarity, match.Similarity)).ToArray(),
            KnowledgeRetrievalMode.InMemoryVector,
            queryResult.DurationMilliseconds,
            search.ElapsedMilliseconds,
            total.ElapsedMilliseconds,
            index);
    }

    private async Task<KnowledgeIndexDiagnostics> EnsureIndexedAsync(CancellationToken cancellationToken)
    {
        if (_index is not null) return _index;
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (_index is not null) return _index;
            var timer = Stopwatch.StartNew();
            var documents = await loader.LoadAsync(cancellationToken);
            var chunks = documents.SelectMany(chunker.Chunk).ToArray();
            var embedded = new List<KnowledgeChunk>(chunks.Length);
            var calls = 0;
            var dimensions = 0;
            long embeddingDuration = 0;
            var embeddingTokens = 0;
            var hasEmbeddingTokens = false;

            foreach (var batch in chunks.Chunk(options.EmbeddingBatchSize))
            {
                var result = await embeddingClient.EmbedBatchAsync(batch.Select(x => x.Text).ToArray(), cancellationToken);
                calls++;
                embeddingDuration += result.DurationMilliseconds;
                if (result.InputTokens is int tokens)
                {
                    embeddingTokens += tokens;
                    hasEmbeddingTokens = true;
                }
                if (result.Embeddings.Count != batch.Length)
                    throw new InvalidOperationException("Embedding count did not match chunk count.");
                for (var i = 0; i < batch.Length; i++)
                {
                    dimensions = result.Embeddings[i].Values.Count;
                    embedded.Add(batch[i] with { Embedding = result.Embeddings[i].Values });
                }
            }

            vectorStore.Replace(embedded);
            timer.Stop();
            _index = new KnowledgeIndexDiagnostics(
                documents.Count, chunks.Length, calls, dimensions, embeddingDuration,
                hasEmbeddingTokens ? embeddingTokens : null, timer.ElapsedMilliseconds, []);
            return _index;
        }
        finally
        {
            _indexLock.Release();
        }
    }
}
