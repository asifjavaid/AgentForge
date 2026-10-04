using Azure.Core;
using Azure.Identity;

namespace AgentForge.Infrastructure.AzureSearch;

internal static class AzureSearchCredentialFactory
{
    public static TokenCredential Create(AzureAiSearchOptions options) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = options.GetTenantId() });
}
