using System.Text;
using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using Microsoft.Extensions.Options;

namespace AgentForge.Infrastructure.Repositories;

public sealed class SearchCodeTool(
    IRepositoryBoundary boundary,
    IOptions<RepositoryAnalysisOptions> options) : IRepositoryTool
{
    private sealed record Arguments(string Query, string Path);
    private sealed record Match(string Path, int Line, string Text);

    private readonly RepositoryAnalysisOptions _options = options.Value;

    public AgentToolDefinition Definition { get; } = new(
        "search_code",
        "Search bounded UTF-8 repository text files for a literal case-insensitive query and return file/line context. Use '.' or an empty path for the repository root.",
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": 200 },
            "path": { "type": "string" }
          },
          "required": ["query", "path"],
          "additionalProperties": false
        }
        """);

    public async Task<RepositoryToolResult> ExecuteAsync(
        RepositoryContext repository,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var arguments = RepositoryToolJson.Deserialize<Arguments>(argumentsJson);
        if (string.IsNullOrWhiteSpace(arguments.Query) || arguments.Query.Length > 200)
        {
            throw new AgentOperationException(AgentFailureKind.InvalidArguments, "Search query length is invalid.");
        }

        var requestedPath = string.IsNullOrWhiteSpace(arguments.Path) ? "." : arguments.Path;
        var start = boundary.ResolvePath(repository, requestedPath);
        var files = File.Exists(start)
            ? [start]
            : EnumerateFiles(start, cancellationToken);
        var matches = new List<Match>();
        var scannedFiles = 0;
        var truncated = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scannedFiles > _options.GetMaxSearchFiles())
            {
                truncated = true;
                break;
            }

            if (boundary.IsSensitiveFile(file) || !boundary.IsSupportedTextFile(file))
            {
                continue;
            }

            try
            {
                boundary.ResolvePath(repository, file);
                using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                var consumedCharacters = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    lineNumber++;
                    consumedCharacters += line.Length;
                    if (consumedCharacters > _options.GetMaxFileBytes())
                    {
                        break;
                    }

                    if (!line.Contains(arguments.Query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add(new Match(
                        Path.GetRelativePath(repository.FullPath, file).Replace('\\', '/'),
                        lineNumber,
                        line.Length <= 300 ? line : $"{line[..300]}…"));

                    if (matches.Count >= _options.GetMaxSearchResults())
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is DecoderFallbackException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (matches.Count >= _options.GetMaxSearchResults())
            {
                break;
            }
        }

        return new RepositoryToolResult(RepositoryToolJson.Serialize(new
        {
            matches,
            truncated,
            scannedFiles = Math.Min(scannedFiles, _options.GetMaxSearchFiles())
        }));
    }

    private IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current).OrderDescending())
            {
                var isLink = File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);
                if (!isLink && !boundary.IsIgnoredDirectory(Path.GetFileName(directory)))
                {
                    pending.Push(directory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(current).Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }
}
