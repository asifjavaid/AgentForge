using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using AgentForge.Application.SoftwareAgents;

namespace AgentForge.Infrastructure.SoftwareAgents;

internal sealed class SoftwareAgentToolDispatcher : ISoftwareAgentToolDispatcher
{
    private readonly ProjectKnowledgeAgentTool _knowledge;
    private readonly IRepositoryToolDispatcher _repositories;
    private readonly IRepositoryBoundary _boundary;
    private readonly SoftwareAgentOptions _options;
    private readonly HashSet<string> _repositoryToolNames;

    public SoftwareAgentToolDispatcher(
        ProjectKnowledgeAgentTool knowledge,
        IRepositoryToolDispatcher repositories,
        IRepositoryBoundary boundary,
        SoftwareAgentOptions options)
    {
        _knowledge = knowledge;
        _repositories = repositories;
        _boundary = boundary;
        _options = options;
        _repositoryToolNames = repositories.Definitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        Definitions = [knowledge.Definition, .. repositories.Definitions];
    }

    public IReadOnlyList<AgentToolDefinition> Definitions { get; }

    public async Task<SoftwareAgentToolResult> DispatchAsync(
        AgentToolCall call,
        CancellationToken cancellationToken = default)
    {
        if (call.Name == _knowledge.Definition.Name)
            return await _knowledge.ExecuteAsync(call.ArgumentsJson, cancellationToken);
        if (!_repositoryToolNames.Contains(call.Name))
            throw new AgentOperationException(
                AgentFailureKind.InvalidTool,
                "The model requested an unregistered tool.");

        var repository = _boundary.ResolveRepository(_options.RepositoryPath);
        var result = await _repositories.DispatchAsync(repository, call, cancellationToken);
        var content = call.Name == "search_code"
            ? BoundSearchResult(result.Content, _options.MaxRepositorySearchResults)
            : result.Content;
        return new SoftwareAgentToolResult(
            content,
            ExtractRepositoryEvidence(call.Name, content),
            "repository");
    }

    internal static string BoundSearchResult(string content, int maximumResults)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
                return content;

            var allMatches = matches.EnumerateArray().Select(match => match.Clone()).ToArray();
            if (allMatches.Length <= maximumResults)
                return content;

            var groups = allMatches
                .GroupBy(match => match.GetProperty("path").GetString() ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group.ToArray())
                .ToArray();
            var selected = new List<JsonElement>(maximumResults);
            for (var index = 0; selected.Count < maximumResults; index++)
            {
                var added = false;
                foreach (var group in groups)
                {
                    if (index >= group.Length)
                        continue;
                    selected.Add(group[index]);
                    added = true;
                    if (selected.Count == maximumResults)
                        break;
                }

                if (!added)
                    break;
            }

            var sourceTruncated = root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean();
            var scannedFiles = root.TryGetProperty("scannedFiles", out var scanned) ? scanned.GetInt32() : 0;
            return JsonSerializer.Serialize(new
            {
                matches = selected,
                truncated = sourceTruncated || selected.Count < allMatches.Length,
                scannedFiles,
                returnedMatches = selected.Count,
                availableMatches = allMatches.Length
            });
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static IReadOnlyList<string> ExtractRepositoryEvidence(string toolName, string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            IEnumerable<string> paths = toolName switch
            {
                "read_file" when root.TryGetProperty("path", out var path) => [path.GetString() ?? string.Empty],
                "search_code" when root.TryGetProperty("matches", out var matches) =>
                    matches.EnumerateArray().Select(item => item.GetProperty("path").GetString() ?? string.Empty),
                "list_files" when root.TryGetProperty("entries", out var entries) =>
                    entries.EnumerateArray().Select(item => item.GetProperty("path").GetString() ?? string.Empty),
                _ => []
            };
            return paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .Select(path => $"repo:{path}")
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
