using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.SoftwareAgents;
using AgentForge.Domain.SoftwareAgents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.SoftwareAgents;

public sealed class AgentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Ask_ReturnsStructuredRun()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/agent/ask", new { question = "Explain DI." });
        response.EnsureSuccessStatusCode();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var run = await response.Content.ReadFromJsonAsync<SoftwareAgentRun>(jsonOptions);
        Assert.NotNull(run);
        Assert.Equal(SoftwareAgentTerminationReason.Completed, run.TerminationReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Ask_RejectsMissingQuestion(string? question)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/agent/ask", new { question });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_ExposesAgentEndpointWithoutToolSelectionProperties()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/agent/ask", out _));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("AskAgentRequest").GetProperty("properties");
        Assert.True(schema.TryGetProperty("question", out _));
        Assert.False(schema.TryGetProperty("useRag", out _));
        Assert.False(schema.TryGetProperty("useRepository", out _));
    }

    private HttpClient CreateClient() => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISoftwareEngineeringAgent>();
            services.AddSingleton<ISoftwareEngineeringAgent>(new FakeAgent());
        })).CreateClient();

    private sealed class FakeAgent : ISoftwareEngineeringAgent
    {
        public Task<SoftwareAgentRun> AskAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SoftwareAgentRun(
                "run-1", "Dependency injection supplies dependencies from outside.", [], [],
                SoftwareAgentTerminationReason.Completed,
                new SoftwareAgentUsage("Fake", "fake", 10, 5, 15),
                1, 1, 0, []));
    }
}
