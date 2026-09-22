using AgentForge.Application.Agents;

namespace AgentForge.Application.Repositories;

public sealed record RepositoryContext(string FullPath, string DisplayName);

public sealed record RepositoryToolResult(string Content);

public interface IRepositoryBoundary
{
    RepositoryContext ResolveRepository(string repositoryPath);

    string ResolvePath(RepositoryContext repository, string path, bool requireDirectory = false);

    bool IsSensitiveFile(string path);

    bool IsIgnoredDirectory(string directoryName);

    bool IsSupportedTextFile(string path);
}

public interface IRepositoryTool
{
    AgentToolDefinition Definition { get; }

    Task<RepositoryToolResult> ExecuteAsync(
        RepositoryContext repository,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryToolDispatcher
{
    IReadOnlyList<AgentToolDefinition> Definitions { get; }

    Task<RepositoryToolResult> DispatchAsync(
        RepositoryContext repository,
        AgentToolCall call,
        CancellationToken cancellationToken = default);
}
