using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Infrastructure.Repositories;

public sealed class RepositoryToolDispatcher : IRepositoryToolDispatcher
{
    private readonly IReadOnlyDictionary<string, IRepositoryTool> _tools;
    private readonly RepositoryAnalysisOptions _options;
    private readonly ILogger<RepositoryToolDispatcher> _logger;

    public RepositoryToolDispatcher(
        IEnumerable<IRepositoryTool> tools,
        IOptions<RepositoryAnalysisOptions> options,
        ILogger<RepositoryToolDispatcher> logger)
    {
        _tools = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
        _options = options.Value;
        _logger = logger;
        Definitions = _tools.Values.Select(tool => tool.Definition).ToArray();
    }

    public IReadOnlyList<AgentToolDefinition> Definitions { get; }

    public async Task<RepositoryToolResult> DispatchAsync(
        RepositoryContext repository,
        AgentToolCall call,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidTool,
                "The model requested an unregistered tool.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(repository, call.ArgumentsJson, cancellationToken);
            var bounded = result.Content.Length <= _options.GetMaxToolResultCharacters()
                ? result
                : new RepositoryToolResult(RepositoryToolJson.Serialize(new
                {
                    truncated = true,
                    content = result.Content[.._options.GetMaxToolResultCharacters()]
                }));

            stopwatch.Stop();
            _logger.LogInformation(
                "Repository tool completed. Tool: {Tool}; DurationMs: {DurationMs}; ResultCharacters: {ResultCharacters}",
                call.Name,
                stopwatch.ElapsedMilliseconds,
                bounded.Content.Length);
            return bounded;
        }
        catch
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Repository tool failed. Tool: {Tool}; DurationMs: {DurationMs}",
                call.Name,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
