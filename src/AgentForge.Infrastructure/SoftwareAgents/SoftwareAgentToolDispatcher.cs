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
        return new SoftwareAgentToolResult(
            result.Content,
            ExtractRepositoryEvidence(call.Name, result.Content),
            "repository");
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
