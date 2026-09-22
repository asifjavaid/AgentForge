using System.Text;
using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using Microsoft.Extensions.Options;

namespace AgentForge.Infrastructure.Repositories;

public sealed class ReadFileTool(
    IRepositoryBoundary boundary,
    IOptions<RepositoryAnalysisOptions> options) : IRepositoryTool
{
    private sealed record Arguments(string Path);

    private readonly RepositoryAnalysisOptions _options = options.Value;

    public AgentToolDefinition Definition { get; } = new(
        "read_file",
        "Read a bounded UTF-8 text file inside the authorized repository. Sensitive and binary files are denied.",
        """
        {
          "type": "object",
          "properties": { "path": { "type": "string" } },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    public async Task<RepositoryToolResult> ExecuteAsync(
        RepositoryContext repository,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var arguments = RepositoryToolJson.Deserialize<Arguments>(argumentsJson);
        var file = boundary.ResolvePath(repository, arguments.Path);

        if (!File.Exists(file))
        {
            throw new AgentOperationException(AgentFailureKind.InvalidArguments, "read_file requires a file path.");
        }

        if (boundary.IsSensitiveFile(file))
        {
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "Sensitive-file access was denied.");
        }

        if (!boundary.IsSupportedTextFile(file))
        {
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "Unsupported or binary file access was denied.");
        }

        var info = new FileInfo(file);
        var maximum = _options.GetMaxFileBytes();
        var count = (int)Math.Min(info.Length, maximum);
        var bytes = new byte[count];

        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(totalRead, count - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (bytes.AsSpan(0, totalRead).Contains((byte)0))
        {
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "Binary file access was denied.");
        }

        string content;
        try
        {
            content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, totalRead)
                .TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.AccessDenied,
                "The file is not supported UTF-8 text.",
                exception);
        }

        return new RepositoryToolResult(RepositoryToolJson.Serialize(new
        {
            path = Path.GetRelativePath(repository.FullPath, file).Replace('\\', '/'),
            content,
            truncated = info.Length > maximum,
            returnedBytes = totalRead,
            totalBytes = info.Length
        }));
    }
}
