using AgentForge.Application.SoftwareAgents;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Infrastructure.SoftwareAgents;

public static class SoftwareAgentRegistration
{
    public static IServiceCollection AddSoftwareAgentTools(this IServiceCollection services)
    {
        services.AddSingleton<ProjectKnowledgeAgentTool>();
        services.AddSingleton<ISoftwareAgentToolDispatcher, SoftwareAgentToolDispatcher>();
        return services;
    }
}
