using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Llm;
using AgentForge.Domain.Requirements;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.Requirements;

public sealed class RequirementsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _factory;

    public RequirementsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Analyze_ReturnsTypedAnalysis_ForValidRequirement()
    {
        var expected = CreateAnalysis();
        using var client = CreateClient(new FakeStructuredOutputClient(expected));

        var response = await client.PostAsJsonAsync(
            "/api/requirements/analyze",
            new
            {
                requirement = "Add forgot-password functionality. The user should receive an email containing a secure password-reset link."
            });

        response.EnsureSuccessStatusCode();
        var analysis = await response.Content.ReadFromJsonAsync<RequirementAnalysis>(SerializerOptions);

        Assert.NotNull(analysis);
        Assert.Equal(RequirementComplexity.Medium, analysis.Complexity);
        Assert.NotEmpty(analysis.FunctionalRequirements);
        Assert.NotEmpty(analysis.SecurityRisks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Analyze_ReturnsBadRequest_ForMissingOrEmptyRequirement(string? requirement)
    {
        using var client = CreateClient(new FakeStructuredOutputClient(CreateAnalysis()));

        var response = await client.PostAsJsonAsync(
            "/api/requirements/analyze",
            new { requirement });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocument_ExposesAnalyzeEndpoint()
    {
        using var client = CreateClient(new FakeStructuredOutputClient(CreateAnalysis()));
        var documentJson = await client.GetStringAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(documentJson);

        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/requirements/analyze", out var endpoint));
        Assert.True(endpoint.TryGetProperty("post", out _));
    }

    [Fact]
    public async Task Analyze_ReturnsSanitizedProblemDetails_WhenProviderFails()
    {
        const string sensitiveProviderMessage = "provider-internal-sensitive-detail";
        var exception = new LlmOperationException(
            LlmFailureKind.Provider,
            sensitiveProviderMessage);
        using var client = CreateClient(new FakeStructuredOutputClient(exception));

        var response = await client.PostAsJsonAsync(
            "/api/requirements/analyze",
            new { requirement = "Add password reset." });
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(sensitiveProviderMessage, responseBody, StringComparison.Ordinal);
        Assert.Contains("could not complete", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateClient(IStructuredOutputClient structuredOutputClient) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStructuredOutputClient>();
                services.AddSingleton(structuredOutputClient);
            }))
            .CreateClient();

    private static RequirementAnalysis CreateAnalysis() => new(
        "Add a secure forgot-password flow.",
        ["Send a password-reset link."],
        ["Do not reveal whether an account exists."],
        ["Use a time-limited, single-use token."],
        ["User enumeration."],
        ["What token lifetime is required?"],
        RequirementComplexity.Medium);

    private sealed class FakeStructuredOutputClient : IStructuredOutputClient
    {
        private readonly RequirementAnalysis? _response;
        private readonly Exception? _exception;

        public FakeStructuredOutputClient(RequirementAnalysis response)
        {
            _response = response;
        }

        public FakeStructuredOutputClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<T> GenerateAsync<T>(
            StructuredOutputRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_exception is not null)
            {
                return Task.FromException<T>(_exception);
            }

            return Task.FromResult((T)(object)_response!);
        }
    }
}
