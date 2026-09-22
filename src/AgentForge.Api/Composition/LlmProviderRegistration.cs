using AgentForge.Application.Agents;
using AgentForge.Application.Llm;
using AgentForge.Infrastructure.AzureFoundry;
using AgentForge.Infrastructure.OpenAI;

namespace AgentForge.Api.Composition;

public static class LlmProviderRegistration
{
    public static IServiceCollection AddLlmProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<OpenAIOptions>()
            .Bind(configuration.GetSection(OpenAIOptions.SectionName));
        services
            .AddOptions<AzureFoundryOptions>()
            .Bind(configuration.GetSection(AzureFoundryOptions.SectionName));

        switch (configuration["AI:Provider"])
        {
            case "OpenAI":
                services.AddSingleton<IStructuredOutputClient, OpenAiStructuredOutputClient>();
                services.AddSingleton<IToolCallingClient, OpenAiToolCallingClient>();
                break;
            case "AzureFoundry":
                services.AddSingleton<IStructuredOutputClient, AzureFoundryStructuredOutputClient>();
                services.AddSingleton<IToolCallingClient, AzureFoundryToolCallingClient>();
                break;
            default:
                throw new InvalidOperationException(
                    "AI:Provider must be configured as OpenAI or AzureFoundry.");
        }

        return services;
    }
}
