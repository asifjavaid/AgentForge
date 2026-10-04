namespace AgentForge.Infrastructure.AzureSearch;

public sealed class AzureSearchAccessException(
    string operation,
    string requiredRole,
    Exception innerException) : Exception(
        $"Azure AI Search denied '{operation}'. Required role: {requiredRole}.", innerException)
{
    public string Operation { get; } = operation;
    public string RequiredRole { get; } = requiredRole;
}
