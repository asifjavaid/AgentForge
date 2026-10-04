using AgentForge.Application.Knowledge;

namespace AgentForge.Infrastructure.AzureSearch;

public sealed class AzureAiSearchOptions
{
    public const string SectionName = "AzureAiSearch";

    public string? Endpoint { get; init; }
    public string IndexName { get; init; } = "agentforge-knowledge";
    public string? TenantId { get; init; }
    public KnowledgeRetrievalMode RetrievalMode { get; init; } = KnowledgeRetrievalMode.Vector;
    public int TopK { get; init; } = 4;

    public Uri GetEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) ||
            !Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0 ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("AzureAiSearch:Endpoint must be a valid HTTPS service endpoint.");
        return endpoint;
    }

    public string GetIndexName()
    {
        var value = IndexName?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 128 ||
            !value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-') ||
            !(char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) ||
            !(char.IsAsciiLetterLower(value[^1]) || char.IsAsciiDigit(value[^1])))
            throw new InvalidOperationException("AzureAiSearch:IndexName is invalid.");
        return value;
    }

    public string? GetTenantId()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) return null;
        if (!Guid.TryParse(TenantId, out var value))
            throw new InvalidOperationException("AzureAiSearch:TenantId is invalid.");
        return value.ToString();
    }

    public void Validate()
    {
        _ = GetEndpoint();
        _ = GetIndexName();
        _ = GetTenantId();
        if (TopK is < 1 or > 50) throw new InvalidOperationException("AzureAiSearch:TopK must be between 1 and 50.");
        if (!Enum.IsDefined(RetrievalMode) || RetrievalMode == KnowledgeRetrievalMode.InMemoryVector)
            throw new InvalidOperationException("AzureAiSearch:RetrievalMode must be Vector, Keyword, Hybrid, or SemanticHybrid.");
    }
}
