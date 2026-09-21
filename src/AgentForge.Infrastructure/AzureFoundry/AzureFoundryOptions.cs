using AgentForge.Application.Llm;

namespace AgentForge.Infrastructure.AzureFoundry;

public sealed class AzureFoundryOptions
{
    public const string SectionName = "AzureFoundry";

    public string? Endpoint { get; init; }

    public string? DeploymentName { get; init; }

    public string? TenantId { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    public Uri GetChatEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) ||
            !Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Host.Length == 0 ||
            endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0)
        {
            throw new LlmOperationException(
                LlmFailureKind.Configuration,
                "Azure Foundry endpoint configuration is invalid.");
        }

        var baseAddress = endpoint.AbsoluteUri.TrimEnd('/');
        if (!baseAddress.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseAddress += "/openai/v1";
        }

        return new Uri(baseAddress + "/", UriKind.Absolute);
    }

    public string GetDeploymentName()
    {
        if (string.IsNullOrWhiteSpace(DeploymentName))
        {
            throw new LlmOperationException(
                LlmFailureKind.Configuration,
                "Azure Foundry deployment configuration is missing.");
        }

        return DeploymentName.Trim();
    }

    public string? GetTenantId()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            return null;
        }

        if (!Guid.TryParse(TenantId, out var tenantId))
        {
            throw new LlmOperationException(
                LlmFailureKind.Configuration,
                "Azure Foundry tenant configuration is invalid.");
        }

        return tenantId.ToString();
    }
}
