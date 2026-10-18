using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Application.MultiAgent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.MultiAgent;

public sealed class WorkflowEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WorkflowEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CreateReturnsLocationAndSafeView()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/workflows", new { objective = "Sensitive objective text" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/workflows/{FakeManager.Id}", response.Headers.Location!.AbsolutePath);
        Assert.DoesNotContain("Sensitive objective text", body, StringComparison.Ordinal);
        Assert.DoesNotContain("executionOwner", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("previousOutputs", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateRejectsMissingObjective(string? objective)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/workflows", new { objective });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LifecycleAndReportRoutesAreAvailable()
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/workflows/{FakeManager.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/workflows/{FakeManager.Id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/workflows/{FakeManager.Id}/cancel", null)).StatusCode);
        var report = await client.GetStringAsync($"/api/workflows/{FakeManager.Id}/report");
        Assert.Contains("reviewed report", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerPreservesExistingAndAddsWorkflowEndpoints()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/agent/ask", out _));
        Assert.True(paths.TryGetProperty("/api/agent/runs", out _));
        Assert.True(paths.TryGetProperty("/api/workflows", out _));
        Assert.True(paths.TryGetProperty("/api/workflows/{workflowId}/resume", out _));
        Assert.True(paths.TryGetProperty("/api/workflows/{workflowId}/cancel", out _));
        Assert.True(paths.TryGetProperty("/api/workflows/{workflowId}/report", out _));
    }

    private HttpClient CreateClient() => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMultiAgentWorkflowManager>();
            services.AddSingleton<IMultiAgentWorkflowManager>(new FakeManager());
        })).CreateClient();

    private sealed class FakeManager : IMultiAgentWorkflowManager
    {
        internal const string Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public Task<MultiAgentWorkflowView> CreateAsync(string objective, CancellationToken cancellationToken = default) =>
            Task.FromResult(View(WorkflowStatus.Created));
        public Task<MultiAgentWorkflowView> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View(WorkflowStatus.Running));
        public Task<MultiAgentWorkflowView> ResumeAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View(WorkflowStatus.Completed));
        public Task<MultiAgentWorkflowView> CancelAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View(WorkflowStatus.Cancelled));
        public Task<string> GetReportAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult("# reviewed report");

        private static MultiAgentWorkflowView View(WorkflowStatus status) => new(
            Id, status, status.ToString(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            0, 0, [], new WorkflowUsageView(0, 0, null, null, null, 0),
            ReviewOutcome.NotApplicable, null, status == WorkflowStatus.Completed ? "# reviewed report" : null, null);
    }
}
