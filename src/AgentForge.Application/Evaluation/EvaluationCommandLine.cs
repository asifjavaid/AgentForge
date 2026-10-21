namespace AgentForge.Application.Evaluation;

public sealed record EvaluationRunOptions(
    string DatasetPath,
    string BaselinePath,
    string OutputDirectory,
    bool Live,
    bool ConfirmLive,
    string? ApiBaseUrl,
    string? CaseId)
{
    public static EvaluationRunOptions Parse(IReadOnlyList<string> args)
    {
        string Value(string name, string fallback)
        {
            var index = -1;
            for (var candidate = 0; candidate < args.Count; candidate++)
            {
                if (string.Equals(args[candidate], name, StringComparison.Ordinal))
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0) return fallback;
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }
        var live = args.Contains("--live", StringComparer.Ordinal);
        var confirm = args.Contains("--confirm-live", StringComparer.Ordinal);
        if (live && !confirm)
            throw new ArgumentException("Live evaluation requires both --live and --confirm-live.");
        var apiBaseUrl = Value("--api-base-url", string.Empty);
        var caseId = Value("--case", string.Empty);
        if (live && (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(caseId)))
            throw new ArgumentException("Live evaluation requires --api-base-url and one explicit --case to bound provider use.");
        return new EvaluationRunOptions(
            Value("--dataset", "evaluation/datasets/agentforge-engineering-v1.json"),
            Value("--baseline", "evaluation/baselines/assessment-08-baseline.json"),
            Value("--output", "evaluation/results"), live, confirm,
            NullIfEmpty(apiBaseUrl), NullIfEmpty(caseId));
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
