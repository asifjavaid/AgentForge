using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed class InMemoryKnowledgeVectorStore : IKnowledgeVectorStore
{
    private IReadOnlyList<KnowledgeChunk> _chunks = [];

    public void Replace(IReadOnlyList<KnowledgeChunk> chunks) =>
        _chunks = chunks?.ToArray() ?? throw new ArgumentNullException(nameof(chunks));

    public IReadOnlyList<KnowledgeSearchResult> Search(
        IReadOnlyList<float> query,
        int topK,
        double minimumSimilarity,
        string? source = null)
    {
        if (topK <= 0) return [];

        return _chunks
            .Where(chunk => source is null || chunk.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .Select(chunk => new KnowledgeSearchResult(chunk, CosineSimilarity(query, chunk.Embedding)))
            .Where(result => result.Similarity >= minimumSimilarity)
            .OrderByDescending(result => result.Similarity)
            .ThenBy(result => result.Chunk.Source, StringComparer.Ordinal)
            .ThenBy(result => result.Chunk.Position)
            .Take(topK)
            .ToArray();
    }

    public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count == 0 || left.Count != right.Count) return 0;

        double dot = 0, leftSquared = 0, rightSquared = 0;
        for (var i = 0; i < left.Count; i++)
        {
            if (!float.IsFinite(left[i]) || !float.IsFinite(right[i])) return 0;
            dot += left[i] * right[i];
            leftSquared += left[i] * left[i];
            rightSquared += right[i] * right[i];
        }

        if (leftSquared <= 0 || rightSquared <= 0) return 0;
        return dot / (Math.Sqrt(leftSquared) * Math.Sqrt(rightSquared));
    }
}
