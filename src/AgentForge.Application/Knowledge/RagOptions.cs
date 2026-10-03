namespace AgentForge.Application.Knowledge;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public string CorpusPath { get; init; } = "knowledge";
    public int ChunkSize { get; init; } = 900;
    public int ChunkOverlap { get; init; } = 150;
    public int TopK { get; init; } = 4;
    public double MinimumSimilarity { get; init; } = 0.35;
    public int MaxDocumentBytes { get; init; } = 131072;
    public int EmbeddingBatchSize { get; init; } = 16;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CorpusPath)) throw new InvalidOperationException("Rag:CorpusPath is required.");
        if (ChunkSize is < 200 or > 8000) throw new InvalidOperationException("Rag:ChunkSize must be between 200 and 8000 characters.");
        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkSize) throw new InvalidOperationException("Rag:ChunkOverlap must be non-negative and smaller than ChunkSize.");
        if (TopK is < 1 or > 20) throw new InvalidOperationException("Rag:TopK must be between 1 and 20.");
        if (MinimumSimilarity is < -1 or > 1) throw new InvalidOperationException("Rag:MinimumSimilarity must be between -1 and 1.");
        if (MaxDocumentBytes is < 1024 or > 5_000_000) throw new InvalidOperationException("Rag:MaxDocumentBytes is outside the permitted range.");
        if (EmbeddingBatchSize is < 1 or > 128) throw new InvalidOperationException("Rag:EmbeddingBatchSize must be between 1 and 128.");
    }
}
