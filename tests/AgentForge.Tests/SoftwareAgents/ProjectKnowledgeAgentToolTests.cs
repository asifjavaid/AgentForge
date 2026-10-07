using AgentForge.Application.Agents;
using AgentForge.Application.Knowledge;
using AgentForge.Application.SoftwareAgents;
using AgentForge.Domain.Knowledge;
using AgentForge.Infrastructure.SoftwareAgents;

namespace AgentForge.Tests.SoftwareAgents;

public sealed class ProjectKnowledgeAgentToolTests
{
    [Fact]
    public async Task Execute_ReturnsBoundedContentAndEvidence()
    {
        var retriever = new FakeRetriever();
        var tool = new ProjectKnowledgeAgentTool(
            retriever,
            new SoftwareAgentOptions { MaxKnowledgeResults = 2 });

        Assert.Contains("\"maximum\": 2", tool.Definition.JsonSchema, StringComparison.Ordinal);

        var result = await tool.ExecuteAsync(
            """{"query":"reset policy","topK":1,"source":"authentication.md"}""",
            CancellationToken.None);

        Assert.Single(result.Evidence);
        Assert.Equal("knowledge:authentication.md#chunk-1", result.Evidence[0]);
        Assert.Contains("controlled content", result.Content, StringComparison.Ordinal);
        Assert.Equal("authentication.md", retriever.LastRequest!.Source);
    }

    [Fact]
    public async Task Execute_RejectsExcessiveTopK()
    {
        var tool = new ProjectKnowledgeAgentTool(
            new FakeRetriever(),
            new SoftwareAgentOptions { MaxKnowledgeResults = 4 });
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => tool.ExecuteAsync(
            """{"query":"reset","topK":5,"source":null}""",
            CancellationToken.None));
        Assert.Equal(AgentFailureKind.InvalidArguments, exception.FailureKind);
    }

    [Theory]
    [InlineData("../authentication.md")]
    [InlineData("source eq 'authentication.md'")]
    [InlineData("authentication.pdf")]
    public async Task Execute_RejectsInvalidOrArbitraryFilter(string source)
    {
        var tool = new ProjectKnowledgeAgentTool(new FakeRetriever(), new SoftwareAgentOptions());
        var json = System.Text.Json.JsonSerializer.Serialize(new { query = "reset", topK = 2, source });
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            tool.ExecuteAsync(json, CancellationToken.None));
        Assert.Equal(AgentFailureKind.InvalidArguments, exception.FailureKind);
    }

    [Fact]
    public async Task Execute_RejectsUnknownArgumentProperties()
    {
        var tool = new ProjectKnowledgeAgentTool(new FakeRetriever(), new SoftwareAgentOptions());
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => tool.ExecuteAsync(
            """{"query":"reset","topK":2,"source":null,"endpoint":"https://attacker.invalid"}""",
            CancellationToken.None));
        Assert.Equal(AgentFailureKind.InvalidArguments, exception.FailureKind);
    }

    private sealed class FakeRetriever : IKnowledgeRetriever
    {
        public KnowledgeRetrieverRequest? LastRequest { get; private set; }

        public Task<KnowledgeRetrieverResult> RetrieveAsync(
            KnowledgeRetrieverRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IReadOnlyList<KnowledgeRetrieverMatch> matches =
            [
                new(new KnowledgeChunk(
                    "authentication", "authentication.md", "chunk-1", "controlled content", 0, []),
                    1, KnowledgeRetrievalMode.Hybrid, 0.03),
                new(new KnowledgeChunk(
                    "security", "security-guidelines.md", "chunk-1", "other content", 0, []),
                    2, KnowledgeRetrievalMode.Hybrid, 0.02)
            ];
            return Task.FromResult(new KnowledgeRetrieverResult(
                matches,
                KnowledgeRetrievalMode.Hybrid,
                1,
                2,
                3,
                new KnowledgeIndexDiagnostics(5, 7, 1, 1536, 1, 10, 2, [])));
        }
    }
}
