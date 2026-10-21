namespace AgentForge.Application.HumanApproval;

public sealed class HumanApprovalOptions
{
    public const string SectionName = "HumanApproval";
    public string StoreProvider { get; init; } = "File";
    public string FilePath { get; init; } = "AgentForge/approvals";
    public string SandboxRoot { get; init; } = "AgentForge/action-sandbox";
    public string EnvironmentName { get; init; } = "LocalSandbox";
    public string PolicyVersion { get; init; } = "A10-v1";
    public int ApprovalLifetimeMinutes { get; init; } = 30;
    public int ExecutionLeaseSeconds { get; init; } = 30;
    public int ExecutionTimeoutSeconds { get; init; } = 10;
    public int MaxRecordBytes { get; init; } = 262_144;
    public int MaxAuditEvents { get; init; } = 100;
    public IReadOnlyList<string> AllowedFlagNames { get; init; } = ["enable-demo-feature"];

    public TimeSpan ApprovalLifetime => TimeSpan.FromMinutes(ApprovalLifetimeMinutes);
    public TimeSpan ExecutionLease => TimeSpan.FromSeconds(ExecutionLeaseSeconds);
    public TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(ExecutionTimeoutSeconds);

    public void Validate()
    {
        if (StoreProvider is not ("File" or "InMemory") || string.IsNullOrWhiteSpace(FilePath) ||
            string.IsNullOrWhiteSpace(SandboxRoot) || string.IsNullOrWhiteSpace(EnvironmentName) ||
            string.IsNullOrWhiteSpace(PolicyVersion) || ApprovalLifetimeMinutes is < 1 or > 1440 ||
            ExecutionLeaseSeconds is < 5 or > 300 || ExecutionTimeoutSeconds is < 1 or > 120 ||
            ExecutionTimeoutSeconds >= ExecutionLeaseSeconds || MaxRecordBytes is < 4096 or > 1_048_576 ||
            MaxAuditEvents is < 16 or > 1000 || AllowedFlagNames is null || AllowedFlagNames.Count == 0 ||
            AllowedFlagNames.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 80))
            throw new InvalidOperationException("Human approval configuration is invalid.");
    }
}
