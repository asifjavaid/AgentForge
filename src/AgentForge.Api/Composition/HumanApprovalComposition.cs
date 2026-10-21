using AgentForge.Api.Authentication;
using AgentForge.Application.HumanApproval;
using AgentForge.Infrastructure.HumanApproval;
using Microsoft.AspNetCore.Authentication;

namespace AgentForge.Api.Composition;

public static class HumanApprovalComposition
{
    public static IServiceCollection AddHumanApproval(this IServiceCollection services,
        IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration.GetSection(HumanApprovalOptions.SectionName)
            .Get<HumanApprovalOptions>() ?? new HumanApprovalOptions();
        var options = new HumanApprovalOptions
        {
            StoreProvider = configured.StoreProvider,
            FilePath = ResolveOutsideApplication(configured.FilePath, environment.ContentRootPath),
            SandboxRoot = ResolveOutsideApplication(configured.SandboxRoot, environment.ContentRootPath),
            EnvironmentName = configured.EnvironmentName,
            PolicyVersion = configured.PolicyVersion,
            ApprovalLifetimeMinutes = configured.ApprovalLifetimeMinutes,
            ExecutionLeaseSeconds = configured.ExecutionLeaseSeconds,
            ExecutionTimeoutSeconds = configured.ExecutionTimeoutSeconds,
            MaxRecordBytes = configured.MaxRecordBytes,
            MaxAuditEvents = configured.MaxAuditEvents,
            AllowedFlagNames = configured.AllowedFlagNames
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<ApprovalRecordValidator>();
        services.AddSingleton<SandboxFeatureFlagExecutor>();
        services.AddSingleton<IControlledActionExecutor>(provider => provider.GetRequiredService<SandboxFeatureFlagExecutor>());
        services.AddSingleton<IActionTargetVersionProvider>(provider => provider.GetRequiredService<SandboxFeatureFlagExecutor>());
        services.AddSingleton<IActionPolicyEvaluator, DeterministicActionPolicyEvaluator>();
        if (options.StoreProvider == "File") services.AddSingleton<IApprovalStore, FileApprovalStore>();
        else services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        services.AddScoped<IActionProposalSourceValidator, WorkflowProposalSourceValidator>();
        services.AddScoped<IActionProposalValidator, SandboxActionProposalValidator>();
        services.AddScoped<IHumanApprovalService, HumanApprovalService>();

        var developmentEnabled = (environment.IsDevelopment() || environment.IsEnvironment("Test")) &&
            configuration.GetValue("DevelopmentAuthentication:Enabled", false);
        services.AddAuthentication(DevelopmentIdentityHandler.SchemeName)
            .AddScheme<DevelopmentIdentityOptions, DevelopmentIdentityHandler>(DevelopmentIdentityHandler.SchemeName,
                auth => auth.Enabled = developmentEnabled);
        services.AddAuthorizationBuilder()
            .AddPolicy(ApprovalPermissions.ProposalCreate, policy => Permission(policy, ApprovalPermissions.ProposalCreate))
            .AddPolicy(ApprovalPermissions.ProposalRead, policy => Permission(policy, ApprovalPermissions.ProposalRead))
            .AddPolicy(ApprovalPermissions.Review, policy => Permission(policy, ApprovalPermissions.Review))
            .AddPolicy(ApprovalPermissions.Approve, policy => Permission(policy, ApprovalPermissions.Approve))
            .AddPolicy(ApprovalPermissions.Reject, policy => Permission(policy, ApprovalPermissions.Reject))
            .AddPolicy(ApprovalPermissions.Execute, policy => Permission(policy, ApprovalPermissions.Execute))
            .AddPolicy(ApprovalPermissions.AuditRead, policy => Permission(policy, ApprovalPermissions.AuditRead));
        return services;
    }

    private static void Permission(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy,
        string permission) => policy.RequireAuthenticatedUser()
            .RequireClaim(DevelopmentIdentityHandler.PermissionClaim, permission);

    private static string ResolveOutsideApplication(string configuredPath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("A controlled-action storage path must be configured.");
        var path = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(Path.GetTempPath(), configuredPath));
        var application = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path.StartsWith(application, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Controlled-action storage must be outside the application repository.");
        return path;
    }
}
