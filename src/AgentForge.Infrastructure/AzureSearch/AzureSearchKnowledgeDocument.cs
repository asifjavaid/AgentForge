using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Infrastructure.AzureSearch;

internal sealed class AzureSearchKnowledgeDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("documentId")]
    public string DocumentId { get; init; } = string.Empty;
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
    [JsonPropertyName("chunkId")]
    public string ChunkId { get; init; } = string.Empty;
    [JsonPropertyName("position")]
    public int Position { get; init; }
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
    [JsonPropertyName("contentVector")]
    public float[] ContentVector { get; init; } = [];

    public static AzureSearchKnowledgeDocument FromChunk(KnowledgeChunk chunk) => new()
    {
        Id = CreateStableId(chunk),
        DocumentId = chunk.DocumentId,
        Source = chunk.Source,
        ChunkId = chunk.ChunkId,
        Position = chunk.Position,
        Content = chunk.Text,
        ContentVector = chunk.Embedding.ToArray()
    };

    public KnowledgeChunk ToChunk() =>
        new(DocumentId, Source, ChunkId, Content, Position, []);

    internal static string CreateStableId(KnowledgeChunk chunk)
    {
        var identity = $"{chunk.DocumentId}\n{chunk.Source}\n{chunk.ChunkId}\n{chunk.Position}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
