using AgentForge.Application.MultiAgent;
using AgentForge.Infrastructure.MultiAgent;

namespace AgentForge.Api.Composition;

public static class MultiAgentWorkflowComposition
{
    public static IServiceCollection AddMultiAgentWorkflows(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var configured = configuration.GetSection(MultiAgentWorkflowOptions.SectionName)
            .Get<MultiAgentWorkflowOptions>() ?? new MultiAgentWorkflowOptions();
        var options = new MultiAgentWorkflowOptions
        {
            StoreProvider = configured.StoreProvider,
            FilePath = Path.IsPathRooted(configured.FilePath)
                ? configured.FilePath
                : Path.Combine(environment.ContentRootPath, configured.FilePath),
            LeaseSeconds = configured.LeaseSeconds,
            MaxPlannedTasks = configured.MaxPlannedTasks,
            MaxDependencyDepth = configured.MaxDependencyDepth,
            MaxConcurrency = configured.MaxConcurrency,
            MaxModelCallsPerSpecialist = configured.MaxModelCallsPerSpecialist,
            MaxTotalModelCalls = configured.MaxTotalModelCalls,
            MaxTotalToolCalls = configured.MaxTotalToolCalls,
            MaxTotalTokens = configured.MaxTotalTokens,
            MaxTaskOutputCharacters = configured.MaxTaskOutputCharacters,
            MaxCheckpointBytes = configured.MaxCheckpointBytes,
            MaxEvents = configured.MaxEvents,
            MaxRevisionCycles = configured.MaxRevisionCycles,
            MaxTaskAttempts = configured.MaxTaskAttempts,
            AttemptTimeoutSeconds = configured.AttemptTimeoutSeconds
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<WorkflowPlanValidator>();
        services.AddSingleton<WorkflowCheckpointValidator>();
        services.AddSingleton<IWorkflowPlanner, SupervisorWorkflowPlanner>();
        services.AddSingleton<SpecialistAgentEngine>();
        services.AddSingleton<ISpecialistAgent, RequirementsAnalystAgent>();
        services.AddSingleton<ISpecialistAgent, CodeAnalysisSpecialistAgent>();
        services.AddSingleton<ISpecialistAgent, SolutionArchitectAgent>();
        services.AddSingleton<ISpecialistAgent, SecurityQualityReviewerAgent>();
        services.AddSingleton<ISpecialistAgent, ReportSynthesisAgent>();
        if (options.StoreProvider == "File")
            services.AddSingleton<IWorkflowCheckpointStore, FileWorkflowCheckpointStore>();
        else
            services.AddSingleton<IWorkflowCheckpointStore, InMemoryWorkflowCheckpointStore>();
        services.AddScoped<IMultiAgentWorkflowManager, MultiAgentWorkflowManager>();
        return services;
    }
}
