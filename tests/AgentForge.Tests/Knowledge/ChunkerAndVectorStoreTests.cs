using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Tests.Knowledge;

public sealed class ChunkerAndVectorStoreTests
{
    [Fact]
    public void Chunker_PreservesMetadataAndCreatesOverlap()
    {
        var options = new RagOptions { ChunkSize = 200, ChunkOverlap = 40 };
        var text = string.Join("\n\n", Enumerable.Repeat("A paragraph with enough meaningful controlled content for deterministic chunking.", 8));
        var chunks = new DeterministicTextChunker(options).Chunk(new KnowledgeDocument("doc", "test.md", text));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal("test.md", chunk.Source));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Position));
        Assert.StartsWith("chunk-", chunks[0].ChunkId);
        Assert.True(chunks[0].Text.Length >= 100);
    }

    [Fact]
    public void Chunker_EmptyDocumentProducesNoChunks()
    {
        var chunks = new DeterministicTextChunker(new RagOptions()).Chunk(new KnowledgeDocument("empty", "empty.md", " \n "));
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunker_PreservesConfiguredOverlapWithoutAParagraphBoundary()
    {
        var options = new RagOptions { ChunkSize = 200, ChunkOverlap = 40 };
        var text = string.Concat(Enumerable.Range(0, 500).Select(index => (char)('a' + index % 26)));

        var chunks = new DeterministicTextChunker(options).Chunk(new KnowledgeDocument("doc", "continuous.txt", text));

        Assert.True(chunks.Count >= 2);
        Assert.Equal(chunks[0].Text[^40..], chunks[1].Text[..40]);
    }

    [Fact]
    public void CosineSimilarity_HandlesIdenticalOrthogonalAndInvalidVectors()
    {
        Assert.Equal(1, InMemoryKnowledgeVectorStore.CosineSimilarity([1, 2], [1, 2]), 10);
        Assert.Equal(0, InMemoryKnowledgeVectorStore.CosineSimilarity([1, 0], [0, 1]), 10);
        Assert.Equal(0, InMemoryKnowledgeVectorStore.CosineSimilarity([0, 0], [1, 1]));
        Assert.Equal(0, InMemoryKnowledgeVectorStore.CosineSimilarity([1], [1, 2]));
        Assert.Equal(0, InMemoryKnowledgeVectorStore.CosineSimilarity([float.NaN], [1]));
    }

    [Fact]
    public void Search_OrdersTopKAppliesThresholdAndPreservesMetadata()
    {
        var store = new InMemoryKnowledgeVectorStore();
        store.Replace([
            Chunk("best.md", "chunk-1", [1, 0]),
            Chunk("second.md", "chunk-2", [0.8f, 0.2f]),
            Chunk("irrelevant.md", "chunk-3", [-1, 0])
        ]);

        var results = store.Search([1, 0], 2, 0.5);

        Assert.Equal(2, results.Count);
        Assert.Equal("best.md", results[0].Chunk.Source);
        Assert.Equal("chunk-2", results[1].Chunk.ChunkId);
        Assert.All(results, result => Assert.True(result.Similarity >= 0.5));
    }

    private static KnowledgeChunk Chunk(string source, string id, IReadOnlyList<float> vector) =>
        new("doc", source, id, "text", 0, vector);
}
