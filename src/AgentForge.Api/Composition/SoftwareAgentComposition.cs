using AgentForge.Application.SoftwareAgents;
using AgentForge.Infrastructure.SoftwareAgents;

namespace AgentForge.Api.Composition;

public static class SoftwareAgentComposition
{
    public static IServiceCollection AddSoftwareEngineeringAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(SoftwareAgentOptions.SectionName)
            .Get<SoftwareAgentOptions>() ?? new SoftwareAgentOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSoftwareAgentTools();
        services.AddScoped<ISoftwareEngineeringAgent, SoftwareEngineeringAgent>();
        return services;
    }
}
