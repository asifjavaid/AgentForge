using AgentForge.Application.Agents;

namespace AgentForge.Infrastructure.Repositories;

public sealed class RepositoryAnalysisOptions
{
    public const string SectionName = "RepositoryAnalysis";

    public string? AllowedRoot { get; set; }

    public int MaxFileBytes { get; set; } = 65_536;

    public int MaxListResults { get; set; } = 200;

    public int MaxSearchResults { get; set; } = 50;

    public int MaxSearchFiles { get; set; } = 2_000;

    public int MaxToolResultCharacters { get; set; } = 100_000;

    public string[] IgnoredDirectories { get; set; } =
        [".git", "bin", "obj", "node_modules", "dist", "build"];

    public string GetAllowedRoot()
    {
        if (string.IsNullOrWhiteSpace(AllowedRoot))
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidRequest,
                "RepositoryAnalysis:AllowedRoot is not configured.");
        }

        try
        {
            var root = Path.GetFullPath(AllowedRoot);
            if (!Directory.Exists(root))
            {
                throw new AgentOperationException(
                    AgentFailureKind.InvalidRequest,
                    "The configured repository root does not exist.");
            }

            return root;
        }
        catch (AgentOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidRequest,
                "The configured repository root is invalid.",
                exception);
        }
    }

    public int GetMaxFileBytes() => Math.Clamp(MaxFileBytes, 1_024, 1_048_576);

    public int GetMaxListResults() => Math.Clamp(MaxListResults, 1, 2_000);

    public int GetMaxSearchResults() => Math.Clamp(MaxSearchResults, 1, 500);

    public int GetMaxSearchFiles() => Math.Clamp(MaxSearchFiles, 1, 20_000);

    public int GetMaxToolResultCharacters() => Math.Clamp(MaxToolResultCharacters, 1_024, 1_000_000);
}
