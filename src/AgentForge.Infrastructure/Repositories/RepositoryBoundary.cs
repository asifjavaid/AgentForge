using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using Microsoft.Extensions.Options;

namespace AgentForge.Infrastructure.Repositories;

public sealed class RepositoryBoundary(IOptions<RepositoryAnalysisOptions> options) : IRepositoryBoundary
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln",
        ".json", ".xml", ".yml", ".yaml", ".md", ".txt", ".config",
        ".props", ".targets", ".js", ".jsx", ".ts", ".tsx", ".css",
        ".scss", ".html", ".htm", ".py", ".java", ".go", ".rs", ".sql",
        ".sh", ".ps1", ".cmd", ".bat", ".toml", ".ini"
    };

    private static readonly HashSet<string> ExtensionlessTextFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "LICENSE", "NOTICE"
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "id_rsa", "id_ed25519"
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pfx", ".p12", ".key", ".pem"
    };

    private readonly RepositoryAnalysisOptions _options = options.Value;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public RepositoryContext ResolveRepository(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw InvalidPath("Repository path is required.");
        }

        var allowedRoot = _options.GetAllowedRoot();
        var candidate = Path.IsPathRooted(repositoryPath)
            ? GetFullPath(repositoryPath)
            : GetFullPath(Path.Combine(allowedRoot, repositoryPath));

        EnsureWithin(allowedRoot, candidate);
        if (!Directory.Exists(candidate))
        {
            throw InvalidPath("The requested repository does not exist.");
        }

        EnsureLinksRemainWithin(allowedRoot, candidate);
        var displayName = Path.GetRelativePath(allowedRoot, candidate);
        if (displayName == ".")
        {
            displayName = new DirectoryInfo(candidate).Name;
        }

        return new RepositoryContext(candidate, displayName);
    }

    public string ResolvePath(RepositoryContext repository, string path, bool requireDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidPath("Tool path is required.");
        }

        var candidate = Path.IsPathRooted(path)
            ? GetFullPath(path)
            : GetFullPath(Path.Combine(repository.FullPath, path));

        EnsureWithin(repository.FullPath, candidate);

        var exists = requireDirectory ? Directory.Exists(candidate) : File.Exists(candidate) || Directory.Exists(candidate);
        if (!exists)
        {
            throw InvalidPath("The requested repository path does not exist.");
        }

        EnsureLinksRemainWithin(repository.FullPath, candidate);
        return candidate;
    }

    public bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path);
        return SensitiveNames.Contains(name) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               SensitiveExtensions.Contains(Path.GetExtension(name));
    }

    public bool IsIgnoredDirectory(string directoryName) =>
        _options.IgnoredDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);

    public bool IsSupportedTextFile(string path)
    {
        var name = Path.GetFileName(path);
        return SupportedExtensions.Contains(Path.GetExtension(name)) ||
               ExtensionlessTextFiles.Contains(name) ||
               name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);
    }

    private string GetFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath("The requested path is malformed.", exception);
        }
    }

    private void EnsureWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", _comparison) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", _comparison) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", _comparison))
        {
            throw new AgentOperationException(
                AgentFailureKind.AccessDenied,
                "Repository access outside the authorized boundary was denied.");
        }
    }

    private void EnsureLinksRemainWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            if (!info.Exists || string.IsNullOrWhiteSpace(info.LinkTarget))
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw new AgentOperationException(
                    AgentFailureKind.AccessDenied,
                    "An unresolved repository link was denied.");
            }

            EnsureWithin(root, GetFullPath(target.FullName));
        }
    }

    private static AgentOperationException InvalidPath(string message, Exception? innerException = null) =>
        innerException is null
            ? new AgentOperationException(AgentFailureKind.InvalidRequest, message)
            : new AgentOperationException(AgentFailureKind.InvalidRequest, message, innerException);
}
