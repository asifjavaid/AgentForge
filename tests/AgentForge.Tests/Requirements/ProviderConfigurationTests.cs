using System.Text.Json;
using AgentForge.Api.Composition;
using AgentForge.Application.Llm;
using AgentForge.Domain.Requirements;
using AgentForge.Infrastructure.AzureFoundry;
using AgentForge.Infrastructure.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests.Requirements;

public sealed class ProviderConfigurationTests
{
    [Theory]
    [InlineData("OpenAI", typeof(OpenAiStructuredOutputClient))]
    [InlineData("AzureFoundry", typeof(AzureFoundryStructuredOutputClient))]
    public void SelectedProvider_RegistersExpectedClient(string provider, Type expectedType)
    {
        using var serviceProvider = CreateServices(provider);
        using var scope = serviceProvider.CreateScope();

        var registeredClient = scope.ServiceProvider.GetRequiredService<IStructuredOutputClient>();

        Assert.IsType(expectedType, registeredClient);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UnknownProvider")]
    public void MissingOrUnknownProvider_FailsAtComposition(string provider)
    {
        var configuration = CreateConfiguration(provider);
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddLlmProvider(configuration));

        Assert.Contains("AI:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.services.ai.azure.com/api/projects/agentforge-dev", "https://example.services.ai.azure.com/api/projects/agentforge-dev/openai/v1/")]
    [InlineData("https://example.openai.azure.com/openai/v1/", "https://example.openai.azure.com/openai/v1/")]
    public void FoundryOptions_NormalizeSupportedEndpointForms(string configured, string expected)
    {
        var options = new AzureFoundryOptions
        {
            Endpoint = configured,
            DeploymentName = "agentforge-gpt5-mini"
        };

        Assert.Equal(expected, options.GetChatEndpoint().AbsoluteUri);
        Assert.Equal("agentforge-gpt5-mini", options.GetDeploymentName());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.openai.azure.com")]
    [InlineData("not-a-uri")]
    public void FoundryOptions_RejectInvalidEndpoint(string? endpoint)
    {
        var options = new AzureFoundryOptions
        {
            Endpoint = endpoint,
            DeploymentName = "agentforge-gpt5-mini"
        };

        var exception = Assert.Throws<LlmOperationException>(() => options.GetChatEndpoint());

        Assert.Equal(LlmFailureKind.Configuration, exception.FailureKind);
    }

    [Fact]
    public void FoundryOptions_RejectMissingDeployment()
    {
        var options = new AzureFoundryOptions
        {
            Endpoint = "https://example.openai.azure.com/openai/v1"
        };

        var exception = Assert.Throws<LlmOperationException>(() => options.GetDeploymentName());

        Assert.Equal(LlmFailureKind.Configuration, exception.FailureKind);
    }

    [Fact]
    public void FoundryOptions_AcceptsOptionalTenantId()
    {
        var options = new AzureFoundryOptions
        {
            TenantId = "729505e5-49dd-468b-b251-cc9cba1af826"
        };

        Assert.Equal("729505e5-49dd-468b-b251-cc9cba1af826", options.GetTenantId());
        Assert.Null(new AzureFoundryOptions().GetTenantId());
    }

    [Fact]
    public void FoundryOptions_RejectsInvalidTenantId()
    {
        var exception = Assert.Throws<LlmOperationException>(() =>
            new AzureFoundryOptions { TenantId = "not-a-tenant-id" }.GetTenantId());

        Assert.Equal(LlmFailureKind.Configuration, exception.FailureKind);
    }

    [Fact]
    public void FoundryStructuredResponse_DeserializesExistingDomainContract()
    {
        const string response = """
            {
              "summary": "Add dark mode.",
              "functionalRequirements": ["Provide a dark mode option."],
              "nonFunctionalRequirements": [],
              "technicalConsiderations": ["Review theme styles."],
              "securityRisks": [],
              "questions": ["Should the preference persist?"],
              "complexity": "Low"
            }
            """;

        var analysis = AzureFoundryStructuredOutputClient
            .DeserializeStructuredResponse<RequirementAnalysis>(response);

        Assert.Equal("Add dark mode.", analysis.Summary);
        Assert.Equal(RequirementComplexity.Low, analysis.Complexity);
        Assert.Single(analysis.FunctionalRequirements);
        Assert.Single(analysis.Questions);
    }

    [Theory]
    [InlineData("{\"summary\":\"Missing required fields\"}")]
    [InlineData("{\"summary\":\"Wrong enum\",\"functionalRequirements\":[],\"nonFunctionalRequirements\":[],\"technicalConsiderations\":[],\"securityRisks\":[],\"questions\":[],\"complexity\":\"Critical\"}")]
    public void FoundryStructuredResponse_RejectsInvalidContract(string response)
    {
        Assert.Throws<JsonException>(() =>
            AzureFoundryStructuredOutputClient.DeserializeStructuredResponse<RequirementAnalysis>(response));
    }

    [Fact]
    public void FoundryStructuredResponse_RejectsNullRequiredCollection()
    {
        const string response = """
            {
              "summary": "Add dark mode.",
              "functionalRequirements": null,
              "nonFunctionalRequirements": [],
              "technicalConsiderations": [],
              "securityRisks": [],
              "questions": [],
              "complexity": "Low"
            }
            """;

        var exception = Assert.Throws<LlmOperationException>(() =>
            AzureFoundryStructuredOutputClient.DeserializeStructuredResponse<RequirementAnalysis>(response));

        Assert.Equal(LlmFailureKind.InvalidResponse, exception.FailureKind);
    }

    private static ServiceProvider CreateServices(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLlmProvider(CreateConfiguration(provider));
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:Provider"] = provider
            })
            .Build();
}
