using System.Text.RegularExpressions;

namespace AgentForge.Infrastructure.DurableAgents;

public sealed partial class AzureTableCheckpointOptions
{
    public const string SectionName = "AzureTableCheckpoint";

    public string? Endpoint { get; init; }
    public string TableName { get; init; } = "AgentForgeAgentRuns";
    public string? TenantId { get; init; }

    public Uri GetEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("AzureTableCheckpoint:Endpoint must be an HTTPS table service endpoint.");
        return endpoint;
    }

    public string GetTableName()
    {
        if (!TableNamePattern().IsMatch(TableName))
            throw new InvalidOperationException("AzureTableCheckpoint:TableName is invalid.");
        return TableName;
    }

    public string? GetTenantId()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) return null;
        if (!Guid.TryParse(TenantId, out var value))
            throw new InvalidOperationException("AzureTableCheckpoint:TenantId is invalid.");
        return value.ToString();
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{2,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex TableNamePattern();
}
