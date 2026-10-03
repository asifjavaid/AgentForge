using System.Text.Json.Serialization;

namespace AgentForge.Domain.Knowledge;

public sealed record KnowledgeDocument(string DocumentId, string Source, string Text);

public sealed record KnowledgeChunk(
    string DocumentId,
    string Source,
    string ChunkId,
    string Text,
    int Position,
    [property: JsonIgnore] IReadOnlyList<float> Embedding);

public sealed record KnowledgeRetrieval(
    string Source,
    string ChunkId,
    int Position,
    double Similarity);

public sealed record KnowledgeSource(string Source, string ChunkId);

public sealed record KnowledgeIndexDiagnostics(
    int DocumentsProcessed,
    int ChunksCreated,
    int EmbeddingCalls,
    int EmbeddingDimensions,
    long EmbeddingDurationMilliseconds,
    int? EmbeddingInputTokens,
    long DurationMilliseconds,
    IReadOnlyList<string> Errors);

public sealed record KnowledgeAskDiagnostics(
    long QueryEmbeddingMilliseconds,
    long RetrievalMilliseconds,
    long GenerationMilliseconds,
    long OverallMilliseconds,
    int? GenerationInputTokens,
    int? GenerationOutputTokens,
    int? GenerationTotalTokens,
    KnowledgeIndexDiagnostics Index);

public sealed record KnowledgeAnswer(
    string Answer,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeRetrieval> Retrieval,
    bool IsSufficientEvidence,
    KnowledgeAskDiagnostics Diagnostics);
