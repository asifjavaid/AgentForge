using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

namespace AgentForge.Infrastructure.AzureSearch;

internal sealed record AzureSearchUploadResult(int Uploaded, IReadOnlyList<string> Errors);

internal sealed record AzureSearchQueryResult(
    AzureSearchKnowledgeDocument Document,
    double Score,
    double? RerankerScore);

internal interface IAzureSearchGateway
{
    Task<SearchIndex?> GetIndexAsync(CancellationToken cancellationToken);
    Task CreateIndexAsync(SearchIndex index, CancellationToken cancellationToken);
    Task<AzureSearchUploadResult> UpsertAsync(
        IReadOnlyList<AzureSearchKnowledgeDocument> documents,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AzureSearchQueryResult>> SearchAsync(
        string? searchText,
        SearchOptions options,
        CancellationToken cancellationToken);
}

internal sealed class AzureSearchGateway(AzureAiSearchOptions options) : IAzureSearchGateway
{
    private readonly SearchIndexClient _indexClient = new(
        options.GetEndpoint(), AzureSearchCredentialFactory.Create(options));
    private readonly SearchClient _searchClient = new(
        options.GetEndpoint(), options.GetIndexName(), AzureSearchCredentialFactory.Create(options));

    public async Task<SearchIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _indexClient.GetIndexAsync(options.GetIndexName(), cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task CreateIndexAsync(SearchIndex index, CancellationToken cancellationToken) =>
        _ = await _indexClient.CreateIndexAsync(index, cancellationToken);

    public async Task<AzureSearchUploadResult> UpsertAsync(
        IReadOnlyList<AzureSearchKnowledgeDocument> documents,
        CancellationToken cancellationToken)
    {
        var response = await _searchClient.MergeOrUploadDocumentsAsync(
            documents, cancellationToken: cancellationToken);
        var errors = response.Value.Results.Where(result => !result.Succeeded)
            .Select(result => $"{result.Key}: {result.ErrorMessage}").ToArray();
        return new AzureSearchUploadResult(
            response.Value.Results.Count(result => result.Succeeded), errors);
    }

    public async Task<IReadOnlyList<AzureSearchQueryResult>> SearchAsync(
        string? searchText,
        SearchOptions searchOptions,
        CancellationToken cancellationToken)
    {
        var response = await _searchClient.SearchAsync<AzureSearchKnowledgeDocument>(
            searchText, searchOptions, cancellationToken);
        var results = new List<AzureSearchQueryResult>();
        await foreach (var result in response.Value.GetResultsAsync())
            results.Add(new AzureSearchQueryResult(
                result.Document,
                result.Score ?? 0,
                result.SemanticSearch?.RerankerScore));
        return results;
    }
}
