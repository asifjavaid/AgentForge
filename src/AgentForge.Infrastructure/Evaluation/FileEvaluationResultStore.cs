using System.Text;
using System.Text.Json;
using AgentForge.Application.Evaluation;

namespace AgentForge.Infrastructure.Evaluation;

public sealed class FileEvaluationResultStore : IEvaluationResultStore
{
    public async Task SaveAsync(
        EvaluationRunResult result,
        string jsonPath,
        string markdownPath,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(jsonPath, JsonSerializer.Serialize(result, JsonEvaluationDatasetLoader.JsonOptions), cancellationToken);
        await WriteAsync(markdownPath, Markdown(result), cancellationToken);
    }

    internal static string Markdown(EvaluationRunResult result)
    {
        var builder = new StringBuilder()
            .AppendLine($"# Evaluation run — {result.DatasetId} v{result.DatasetVersion}")
            .AppendLine()
            .AppendLine($"- Outcome: **{result.Outcome}**")
            .AppendLine($"- Average quality: **{result.AverageQualityScore:F2}/100**")
            .AppendLine($"- Cases: {result.Cases.Count} ({result.Passed} pass, {result.PassedWithConcerns} concerns, {result.Failed} fail, {result.NotExecuted} not executed)")
            .AppendLine($"- Usage: {result.AggregateUsage.ModelCalls} model calls, {result.AggregateUsage.ToolCalls} tool calls, {result.AggregateUsage.TotalTokens?.ToString() ?? "unknown"} tokens")
            .AppendLine()
            .AppendLine("| Case | Outcome | Quality | Citation validity | Security violations |")
            .AppendLine("|---|---:|---:|---:|---:|");
        foreach (var item in result.Cases)
            builder.AppendLine($"| {item.CaseId} | {item.Outcome} | {item.Scores.QualityScore:F2} | {item.Retrieval.CitationValidity:F2}% | {item.SecurityViolations.Count} |");
        if (result.BaselineComparison is not null)
        {
            builder.AppendLine().AppendLine("## Baseline comparison").AppendLine()
                .AppendLine($"Comparable: {result.BaselineComparison.Comparable}. {result.BaselineComparison.Reason}")
                .AppendLine($"Token change: {Format(result.BaselineComparison.TokenChangePercent)}; duration change: {Format(result.BaselineComparison.DurationChangePercent)}.");
        }
        return builder.ToString();
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
    }

    private static string Format(double? value) => value is null ? "unknown" : $"{value:F2}%";
}
