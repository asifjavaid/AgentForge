using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed class DeterministicTextChunker(RagOptions options) : IKnowledgeChunker
{
    public IReadOnlyList<KnowledgeChunk> Chunk(KnowledgeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.Text)) return [];

        var text = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var chunks = new List<KnowledgeChunk>();
        var start = 0;
        var position = 0;

        while (start < text.Length)
        {
            var proposedEnd = Math.Min(start + options.ChunkSize, text.Length);
            var end = proposedEnd;
            if (proposedEnd < text.Length)
            {
                var boundary = text.LastIndexOf("\n\n", proposedEnd - 1, proposedEnd - start, StringComparison.Ordinal);
                if (boundary > start + options.ChunkSize / 2) end = boundary;
            }

            var value = text[start..end].Trim();
            if (value.Length > 0)
            {
                chunks.Add(new KnowledgeChunk(
                    document.DocumentId,
                    document.Source,
                    $"chunk-{position + 1}",
                    value,
                    position,
                    []));
                position++;
            }

            if (end >= text.Length) break;
            var next = Math.Max(start + 1, end - options.ChunkOverlap);
            var paragraphAfterOverlap = text.IndexOf("\n\n", next, StringComparison.Ordinal);
            if (paragraphAfterOverlap >= next && paragraphAfterOverlap < end)
            {
                next = paragraphAfterOverlap + 2;
            }
            start = next;
        }

        return chunks;
    }
}
