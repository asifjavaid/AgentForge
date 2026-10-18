using AgentForge.Application.DurableAgents;
using AgentForge.Infrastructure.DurableAgents;

namespace AgentForge.Api.Composition;

public static class DurableAgentComposition
{
    public static IServiceCollection AddDurableAgentRuns(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var configured = configuration.GetSection(DurableAgentOptions.SectionName)
            .Get<DurableAgentOptions>() ?? new DurableAgentOptions();
        var options = new DurableAgentOptions
        {
            StoreProvider = configured.StoreProvider,
            FilePath = Path.IsPathRooted(configured.FilePath)
                ? configured.FilePath
                : Path.Combine(environment.ContentRootPath, configured.FilePath),
            LeaseSeconds = configured.LeaseSeconds,
            MaxCheckpointBytes = configured.MaxCheckpointBytes,
            MaxConversationMessages = configured.MaxConversationMessages
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DurableCheckpointValidator>();

        switch (options.StoreProvider)
        {
            case "File":
                services.AddSingleton<IAgentCheckpointStore, FileAgentCheckpointStore>();
                break;
            case "InMemory":
                services.AddSingleton<IAgentCheckpointStore, InMemoryAgentCheckpointStore>();
                break;
            case "AzureTable":
                var azureOptions = configuration.GetSection(AzureTableCheckpointOptions.SectionName)
                    .Get<AzureTableCheckpointOptions>() ?? new AzureTableCheckpointOptions();
                azureOptions.GetEndpoint();
                azureOptions.GetTableName();
                azureOptions.GetTenantId();
                services.AddSingleton(azureOptions);
                services.AddSingleton<IAgentCheckpointStore, AzureTableAgentCheckpointStore>();
                break;
        }

        services.AddScoped<IDurableAgentRunManager, DurableAgentRunManager>();
        return services;
    }
}
