using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed partial class KnowledgeService(
    IKnowledgeRetriever retriever,
    IGroundedGenerationClient generationClient) : IKnowledgeService
{
    public async Task<KnowledgeAnswer> AskAsync(
        string question,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question is required.", nameof(question));
        source = KnowledgeFilterValidation.ValidateSource(source);

        var overall = Stopwatch.StartNew();
        var retrieved = await retriever.RetrieveAsync(
            new KnowledgeRetrieverRequest(question.Trim(), source), cancellationToken);
        var retrieval = retrieved.Matches.Select(ToRetrieval).ToArray();

        if (retrieved.Matches.Count == 0)
        {
            overall.Stop();
            return new KnowledgeAnswer(
                "The available project knowledge does not provide enough information to answer this question.",
                [], retrieval, false,
                new KnowledgeAskDiagnostics(
                    retrieved.Mode.ToString(), retrieved.QueryEmbeddingMilliseconds,
                    retrieved.SearchMilliseconds, 0, overall.ElapsedMilliseconds,
                    null, null, null, retrieved.Index));
        }

        var generation = await generationClient.GenerateAsync(
            new GroundedGenerationRequest(
                KnowledgePrompt.SystemMessage,
                question.Trim(),
                retrieved.Matches.Select(x => x.Chunk).ToArray()),
            cancellationToken);

        var allowed = retrieved.Matches.ToDictionary(x => Identifier(x.Chunk), StringComparer.Ordinal);
        var citations = generation.Citations.Distinct(StringComparer.Ordinal).ToArray();
        if (!generation.IsSufficientEvidence && citations.Length > 0)
            throw new InvalidOperationException("An insufficient-evidence response cannot contain citations.");
        if ((generation.IsSufficientEvidence && citations.Length == 0) ||
            citations.Any(citation => !allowed.ContainsKey(citation)))
            throw new InvalidOperationException("The grounded generation response contained missing or invalid citations.");

        var markers = CitationPattern().Matches(generation.Answer).Select(match => match.Groups[1].Value).ToArray();
        if (markers.Any(marker => !allowed.ContainsKey(marker)))
            throw new InvalidOperationException("The grounded answer cited a source that was not retrieved.");

        var answer = generation.Answer.Trim();
        foreach (var citation in citations.Where(citation => !answer.Contains($"[{citation}]", StringComparison.Ordinal)))
            answer += $" [{citation}]";

        var sources = citations.Select(citation =>
        {
            var chunk = allowed[citation].Chunk;
            return new KnowledgeSource(chunk.Source, chunk.ChunkId);
        }).ToArray();

        overall.Stop();
        return new KnowledgeAnswer(
            answer, sources, retrieval, generation.IsSufficientEvidence,
            new KnowledgeAskDiagnostics(
                retrieved.Mode.ToString(), retrieved.QueryEmbeddingMilliseconds,
                retrieved.SearchMilliseconds, generation.DurationMilliseconds,
                overall.ElapsedMilliseconds, generation.InputTokens,
                generation.OutputTokens, generation.TotalTokens, retrieved.Index));
    }

    private static KnowledgeRetrieval ToRetrieval(KnowledgeRetrieverMatch result) =>
        new(
            result.Chunk.Source,
            result.Chunk.ChunkId,
            result.Chunk.Position,
            result.Rank,
            result.Mode.ToString(),
            Math.Round(result.Score, 6),
            result.Similarity is double similarity ? Math.Round(similarity, 6) : null,
            result.RerankerScore is double reranker ? Math.Round(reranker, 6) : null);

    private static string Identifier(KnowledgeChunk chunk) => $"{chunk.Source}#{chunk.ChunkId}";

    [GeneratedRegex(@"\[([^\[\]]+\.(?:md|txt)#chunk-\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CitationPattern();
}
