using AgentForge.Application.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Infrastructure.AzureSearch;

public static class AzureSearchRegistration
{
    public static IServiceCollection AddAzureAiSearchKnowledge(
        this IServiceCollection services,
        AzureAiSearchOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IAzureSearchGateway, AzureSearchGateway>();
        services.AddSingleton<AzureSearchIndexState>();
        services.AddSingleton<IKnowledgeRetriever, AzureAiSearchKnowledgeRetriever>();
        services.AddSingleton<IKnowledgeIndexManager, AzureAiSearchIndexManager>();
        return services;
    }
}
