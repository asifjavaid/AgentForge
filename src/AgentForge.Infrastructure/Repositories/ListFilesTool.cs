using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using Microsoft.Extensions.Options;

namespace AgentForge.Infrastructure.Repositories;

public sealed class ListFilesTool(
    IRepositoryBoundary boundary,
    IOptions<RepositoryAnalysisOptions> options) : IRepositoryTool
{
    private sealed record Arguments(string Path, bool Recursive);
    private sealed record Entry(string Path, string Type);

    private readonly RepositoryAnalysisOptions _options = options.Value;

    public AgentToolDefinition Definition { get; } = new(
        "list_files",
        "List bounded files and directories within the authorized repository. Use '.' or an empty path for the repository root. Generated directories are skipped during recursion.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "recursive": { "type": "boolean" }
          },
          "required": ["path", "recursive"],
          "additionalProperties": false
        }
        """);

    public Task<RepositoryToolResult> ExecuteAsync(
        RepositoryContext repository,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var arguments = RepositoryToolJson.Deserialize<Arguments>(argumentsJson);
        var requestedPath = string.IsNullOrWhiteSpace(arguments.Path) ? "." : arguments.Path;
        var directory = boundary.ResolvePath(repository, requestedPath, requireDirectory: true);
        var entries = new List<Entry>();
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        var truncated = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();

            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = Directory.Exists(path);
                if (isDirectory && boundary.IsIgnoredDirectory(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    boundary.ResolvePath(repository, path, requireDirectory: isDirectory);
                }
                catch (AgentOperationException exception) when (exception.FailureKind == AgentFailureKind.AccessDenied)
                {
                    continue;
                }

                entries.Add(new Entry(
                    Path.GetRelativePath(repository.FullPath, path).Replace('\\', '/'),
                    isDirectory ? "directory" : "file"));

                if (entries.Count >= _options.GetMaxListResults())
                {
                    truncated = true;
                    pending.Clear();
                    break;
                }

                var isLink = isDirectory &&
                    File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
                if (arguments.Recursive && isDirectory && !isLink)
                {
                    pending.Enqueue(path);
                }
            }

            if (!arguments.Recursive)
            {
                break;
            }
        }

        return Task.FromResult(new RepositoryToolResult(
            RepositoryToolJson.Serialize(new { entries, truncated })));
    }
}
