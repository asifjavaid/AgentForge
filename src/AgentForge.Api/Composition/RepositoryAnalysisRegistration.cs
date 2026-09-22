using AgentForge.Application.Repositories;
using AgentForge.Infrastructure.Repositories;

namespace AgentForge.Api.Composition;

public static class RepositoryAnalysisRegistration
{
    public static IServiceCollection AddRepositoryAnalysis(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RepositoryAnalysisOptions>()
            .Bind(configuration.GetSection(RepositoryAnalysisOptions.SectionName));

        var agentOptions = configuration
            .GetSection(RepositoryAgentOptions.SectionName)
            .Get<RepositoryAgentOptions>() ?? new RepositoryAgentOptions();
        services.AddSingleton(agentOptions);

        services.AddSingleton<IRepositoryBoundary, RepositoryBoundary>();
        services.AddSingleton<IRepositoryTool, ListFilesTool>();
        services.AddSingleton<IRepositoryTool, ReadFileTool>();
        services.AddSingleton<IRepositoryTool, SearchCodeTool>();
        services.AddSingleton<IRepositoryToolDispatcher, RepositoryToolDispatcher>();
        services.AddScoped<IRepositoryAnalysisAgent, RepositoryAnalysisAgent>();
        return services;
    }
}
