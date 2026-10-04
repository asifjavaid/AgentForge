using AgentForge.Domain.Knowledge;

namespace AgentForge.Application.Knowledge;

public sealed record EmbeddingVector(IReadOnlyList<float> Values);

public sealed record EmbeddingBatchResult(
    IReadOnlyList<EmbeddingVector> Embeddings,
    string Provider,
    string Model,
    long DurationMilliseconds,
    int? InputTokens);

public interface IEmbeddingClient
{
    Task<EmbeddingBatchResult> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<EmbeddingBatchResult> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public sealed record GroundedGenerationRequest(
    string SystemPrompt,
    string Question,
    IReadOnlyList<KnowledgeChunk> Context);

public sealed record GroundedGenerationResult(
    string Answer,
    IReadOnlyList<string> Citations,
    bool IsSufficientEvidence,
    string Provider,
    string Model,
    long DurationMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);

public interface IGroundedGenerationClient
{
    Task<GroundedGenerationResult> GenerateAsync(
        GroundedGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeDocumentLoader
{
    Task<IReadOnlyList<KnowledgeDocument>> LoadAsync(CancellationToken cancellationToken = default);
}

public interface IKnowledgeChunker
{
    IReadOnlyList<KnowledgeChunk> Chunk(KnowledgeDocument document);
}

public interface IKnowledgeVectorStore
{
    void Replace(IReadOnlyList<KnowledgeChunk> chunks);
    IReadOnlyList<KnowledgeSearchResult> Search(
        IReadOnlyList<float> query,
        int topK,
        double minimumSimilarity,
        string? source = null);
}

public sealed record KnowledgeSearchResult(KnowledgeChunk Chunk, double Similarity);

public enum KnowledgeRetrieverKind
{
    InMemory,
    AzureAiSearch
}

public enum KnowledgeRetrievalMode
{
    InMemoryVector,
    Vector,
    Keyword,
    Hybrid,
    SemanticHybrid
}

public sealed record KnowledgeRetrieverRequest(string Question, string? Source);

public sealed record KnowledgeRetrieverMatch(
    KnowledgeChunk Chunk,
    int Rank,
    KnowledgeRetrievalMode Mode,
    double Score,
    double? Similarity = null,
    double? RerankerScore = null);

public sealed record KnowledgeRetrieverResult(
    IReadOnlyList<KnowledgeRetrieverMatch> Matches,
    KnowledgeRetrievalMode Mode,
    long QueryEmbeddingMilliseconds,
    long SearchMilliseconds,
    long TotalMilliseconds,
    KnowledgeIndexDiagnostics Index);

public interface IKnowledgeRetriever
{
    Task<KnowledgeRetrieverResult> RetrieveAsync(
        KnowledgeRetrieverRequest request,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeIndexManager
{
    Task<KnowledgeIndexingResult> InitializeAndIndexAsync(CancellationToken cancellationToken = default);
}

public interface IKnowledgeService
{
    Task<KnowledgeAnswer> AskAsync(
        string question,
        string? source = null,
        CancellationToken cancellationToken = default);
}
