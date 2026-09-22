using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Application.Repositories;
using AgentForge.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.Repositories;

public sealed class RepositoriesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RepositoriesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Analyze_ReturnsTypedRepositoryAnalysis()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/repositories/analyze",
            new { repositoryPath = "SafeRepository", request = "Analyze architecture." });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RepositoryAnalysisRun>();
        Assert.NotNull(result);
        Assert.Equal("Fake", result.Metadata.Provider);
        Assert.Equal(2, result.Metadata.Iterations);
    }

    [Theory]
    [InlineData(null, "Analyze architecture.")]
    [InlineData("SafeRepository", null)]
    [InlineData(" ", " ")]
    public async Task Analyze_RejectsMissingInput(string? repositoryPath, string? request)
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/repositories/analyze",
            new { repositoryPath, request });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocument_ExposesRepositoryAnalysisEndpoint()
    {
        using var client = CreateClient();
        var documentJson = await client.GetStringAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(documentJson);

        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/repositories/analyze", out var endpoint));
        Assert.True(endpoint.TryGetProperty("post", out _));
    }

    private HttpClient CreateClient() =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRepositoryAnalysisAgent>();
                services.AddSingleton<IRepositoryAnalysisAgent>(new FakeAgent());
            }))
            .CreateClient();

    private sealed class FakeAgent : IRepositoryAnalysisAgent
    {
        public Task<RepositoryAnalysisRun> AnalyzeAsync(
            string repositoryPath,
            string request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryAnalysisRun(
                new RepositoryAnalysis(
                    "Fixture summary.",
                    ["Component"],
                    ["README.md"],
                    [],
                    [],
                    [],
                    []),
                new RepositoryAnalysisMetadata(
                    "Fake",
                    "fake-model",
                    2,
                    ["list_files"],
                    10,
                    10,
                    5,
                    15)));
    }
}
