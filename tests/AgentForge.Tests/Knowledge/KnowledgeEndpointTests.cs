using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.Knowledge;

public sealed class KnowledgeEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public KnowledgeEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Ask_ReturnsGroundedAnswer()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/knowledge/ask", new { question = "How long are reset links valid?" });
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<KnowledgeAnswer>();
        Assert.NotNull(answer);
        Assert.True(answer.IsSufficientEvidence);
        Assert.Single(answer.Sources);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ask_RejectsMissingQuestion(string? question)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/knowledge/ask", new { question });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocument_ExposesKnowledgeEndpoint()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/knowledge/ask", out var endpoint));
        Assert.True(endpoint.TryGetProperty("post", out _));
    }

    private HttpClient CreateClient() => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IKnowledgeService>();
            services.AddSingleton<IKnowledgeService>(new FakeKnowledgeService());
        })).CreateClient();

    private sealed class FakeKnowledgeService : IKnowledgeService
    {
        public Task<KnowledgeAnswer> AskAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeAnswer(
                "Thirty minutes [authentication.md#chunk-1]",
                [new("authentication.md", "chunk-1")],
                [new("authentication.md", "chunk-1", 0, 0.9)], true,
                new KnowledgeAskDiagnostics(1, 1, 1, 3, 10, 5, 15,
                    new KnowledgeIndexDiagnostics(5, 5, 1, 3, 2, 20, 2, []))));
    }
}
