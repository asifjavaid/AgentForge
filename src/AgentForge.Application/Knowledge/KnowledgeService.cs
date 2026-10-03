using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed partial class KnowledgeService(
    IKnowledgeDocumentLoader loader,
    IKnowledgeChunker chunker,
    IEmbeddingClient embeddingClient,
    IKnowledgeVectorStore vectorStore,
    IGroundedGenerationClient generationClient,
    RagOptions options) : IKnowledgeService
{
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private KnowledgeIndexDiagnostics? _index;

    public async Task<KnowledgeAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Question is required.", nameof(question));
        var overall = Stopwatch.StartNew();
        var index = await EnsureIndexedAsync(cancellationToken);

        var queryResult = await embeddingClient.EmbedAsync(question.Trim(), cancellationToken);
        var queryVector = queryResult.Embeddings.Single().Values;

        var retrievalTimer = Stopwatch.StartNew();
        var matches = vectorStore.Search(queryVector, options.TopK, options.MinimumSimilarity);
        retrievalTimer.Stop();
        var retrieval = matches.Select(ToRetrieval).ToArray();

        if (matches.Count == 0)
        {
            overall.Stop();
            return new KnowledgeAnswer(
                "The available project knowledge does not provide enough information to answer this question.",
                [], retrieval, false,
                new KnowledgeAskDiagnostics(
                    queryResult.DurationMilliseconds, retrievalTimer.ElapsedMilliseconds, 0,
                    overall.ElapsedMilliseconds, null, null, null, index));
        }

        var generation = await generationClient.GenerateAsync(
            new GroundedGenerationRequest(KnowledgePrompt.SystemMessage, question.Trim(), matches.Select(x => x.Chunk).ToArray()),
            cancellationToken);

        var allowed = matches.ToDictionary(x => Identifier(x.Chunk), StringComparer.Ordinal);
        var citations = generation.Citations.Distinct(StringComparer.Ordinal).ToArray();
        if (!generation.IsSufficientEvidence && citations.Length > 0)
        {
            throw new InvalidOperationException("An insufficient-evidence response cannot contain citations.");
        }

        if ((generation.IsSufficientEvidence && citations.Length == 0) || citations.Any(citation => !allowed.ContainsKey(citation)))
        {
            throw new InvalidOperationException("The grounded generation response contained missing or invalid citations.");
        }

        var markers = CitationPattern().Matches(generation.Answer).Select(match => match.Groups[1].Value).ToArray();
        if (markers.Any(marker => !allowed.ContainsKey(marker)))
        {
            throw new InvalidOperationException("The grounded answer cited a source that was not retrieved.");
        }

        var answer = generation.Answer.Trim();
        foreach (var citation in citations.Where(citation => !answer.Contains($"[{citation}]", StringComparison.Ordinal)))
        {
            answer += $" [{citation}]";
        }

        var sources = citations.Select(citation =>
        {
            var chunk = allowed[citation].Chunk;
            return new KnowledgeSource(chunk.Source, chunk.ChunkId);
        }).ToArray();

        overall.Stop();
        return new KnowledgeAnswer(
            answer, sources, retrieval, generation.IsSufficientEvidence,
            new KnowledgeAskDiagnostics(
                queryResult.DurationMilliseconds, retrievalTimer.ElapsedMilliseconds,
                generation.DurationMilliseconds, overall.ElapsedMilliseconds,
                generation.InputTokens, generation.OutputTokens, generation.TotalTokens, index));
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
                if (result.Embeddings.Count != batch.Length) throw new InvalidOperationException("Embedding count did not match chunk count.");
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

    private static KnowledgeRetrieval ToRetrieval(KnowledgeSearchResult result) =>
        new(result.Chunk.Source, result.Chunk.ChunkId, result.Chunk.Position, Math.Round(result.Similarity, 6));

    private static string Identifier(KnowledgeChunk chunk) => $"{chunk.Source}#{chunk.ChunkId}";

    [GeneratedRegex(@"\[([^\[\]]+\.md#chunk-\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CitationPattern();
}
