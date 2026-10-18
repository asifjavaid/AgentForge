using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Application.DurableAgents;
using AgentForge.Infrastructure.DurableAgents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.DurableAgents;

public sealed class DurableAgentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DurableAgentEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CreateReturnsLocationAndSafeRunView()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/agent/runs", new { question = "What is documented?" });
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/agent/runs/{FakeManager.RunId}", response.Headers.Location!.AbsolutePath);
        Assert.DoesNotContain("What is documented?", json, StringComparison.Ordinal);
        Assert.DoesNotContain("argumentsJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultContent", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateRejectsMissingQuestion(string? question)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/agent/runs", new { question });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetResumeAndCancelRoutesAreAvailable()
    {
        using var client = CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/agent/runs/{FakeManager.RunId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/agent/runs/{FakeManager.RunId}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/agent/runs/{FakeManager.RunId}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task SwaggerExposesDurableLifecycleAlongsideExistingAskEndpoint()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/agent/ask", out _));
        Assert.True(paths.TryGetProperty("/api/agent/runs", out _));
        Assert.True(paths.TryGetProperty("/api/agent/runs/{runId}", out _));
        Assert.True(paths.TryGetProperty("/api/agent/runs/{runId}/resume", out _));
        Assert.True(paths.TryGetProperty("/api/agent/runs/{runId}/cancel", out _));
    }

    [Fact]
    public async Task CreateAndGetUsePersistedCheckpointThroughRealManager()
    {
        var store = new InMemoryAgentCheckpointStore();
        using var client = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentCheckpointStore>();
                services.AddSingleton<IAgentCheckpointStore>(store);
            })).CreateClient();

        var createdResponse = await client.PostAsJsonAsync(
            "/api/agent/runs", new { question = "Persist this run." });
        createdResponse.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var runId = createdJson.RootElement.GetProperty("runId").GetString()!;
        var getResponse = await client.GetAsync($"/api/agent/runs/{runId}");

        getResponse.EnsureSuccessStatusCode();
        Assert.NotNull(await store.GetAsync(runId));
    }

    private HttpClient CreateClient() => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDurableAgentRunManager>();
            services.AddSingleton<IDurableAgentRunManager>(new FakeManager());
        })).CreateClient();

    private sealed class FakeManager : IDurableAgentRunManager
    {
        internal const string RunId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public Task<DurableAgentRunView> CreateAsync(
            string question,
            CancellationToken cancellationToken = default) => Task.FromResult(View(DurableAgentRunStatus.Created));

        public Task<DurableAgentRunView> GetAsync(
            string runId,
            CancellationToken cancellationToken = default) => Task.FromResult(View(DurableAgentRunStatus.Created));

        public Task<DurableAgentRunView> ResumeAsync(
            string runId,
            CancellationToken cancellationToken = default) => Task.FromResult(View(DurableAgentRunStatus.Completed));

        public Task<DurableAgentRunView> CancelAsync(
            string runId,
            CancellationToken cancellationToken = default) => Task.FromResult(View(DurableAgentRunStatus.Cancelled));

        private static DurableAgentRunView View(DurableAgentRunStatus status) => new(
            RunId,
            status,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new DurableAgentProgress(0, 0, 0, 0),
            [],
            null,
            null);
    }
}
