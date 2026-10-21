namespace AgentForge.Application.Evaluation;

public sealed class EvaluationRunner(
    IEvaluationCaseExecutor executor,
    EvaluationScoring scoring,
    TimeProvider timeProvider)
{
    public async Task<EvaluationRunResult> RunAsync(
        EvaluationDataset dataset,
        EvaluationBaseline? baseline = null,
        EvaluationExecutionMode mode = EvaluationExecutionMode.OfflineScripted,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetUtcNow();
        var results = new List<EvaluationCaseResult>(dataset.Cases.Count);
        foreach (var testCase in dataset.Cases)
        {
            try
            {
                results.Add(scoring.Score(testCase,
                    await executor.ExecuteAsync(testCase, cancellationToken)));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                results.Add(NotExecuted(testCase.Id));
            }
        }

        var aggregate = Aggregate(results.Select(result => result.Usage));
        var failed = results.Count(result => result.Outcome == EvaluationOutcome.Fail);
        var notExecuted = results.Count(result => result.Outcome == EvaluationOutcome.NotExecuted);
        var concerns = results.Count(result => result.Outcome == EvaluationOutcome.PassWithConcerns);
        var outcome = failed > 0 ? EvaluationOutcome.Fail : notExecuted > 0
            ? EvaluationOutcome.NotExecuted : concerns > 0 ? EvaluationOutcome.PassWithConcerns : EvaluationOutcome.Pass;
        var comparableCase = dataset.Cases.FirstOrDefault(item => item.Tags.Contains("assessment-08-matched", StringComparer.Ordinal));
        var comparableResult = comparableCase is null ? null : results.Single(result => result.CaseId == comparableCase.Id);
        var comparison = baseline is null || comparableResult is null
            ? null
            : mode == EvaluationExecutionMode.OfflineScripted
                ? new BaselineComparison(baseline.BaselineId, false,
                    "The current run is scripted offline data; the historical baseline is a live Foundry observation.",
                    null, null, null, null, null)
                : Compare(baseline, comparableCase!.Objective, comparableResult);
        return new EvaluationRunResult(dataset.DatasetId, dataset.Version, started, timeProvider.GetUtcNow(),
            results, outcome, Math.Round(results.Where(item => item.Outcome != EvaluationOutcome.NotExecuted)
                .Select(item => item.Scores.QualityScore).DefaultIfEmpty().Average(), 2),
            results.Count(item => item.Outcome == EvaluationOutcome.Pass), concerns, failed, notExecuted,
            aggregate, comparison);
    }

    public static BaselineComparison Compare(
        EvaluationBaseline baseline,
        string objective,
        EvaluationCaseResult current)
    {
        if (!string.Equals(baseline.Objective, objective, StringComparison.Ordinal))
            return new BaselineComparison(baseline.BaselineId, false, "Objectives are not identical.",
                null, null, null, null, null);
        return new BaselineComparison(baseline.BaselineId, true,
            baseline.SingleObservation ? "Matched single-run comparison; not statistically significant." : "Matched comparison.",
            Change(current.Usage.TotalTokens, baseline.TotalTokens),
            Change(current.Usage.DurationMilliseconds, baseline.DurationMilliseconds),
            current.Usage.ModelCalls - baseline.ModelCalls,
            current.Usage.ToolCalls - baseline.ToolCalls,
            null);
    }

    private static EvaluationCaseResult NotExecuted(string id) => new(id, EvaluationOutcome.NotExecuted,
        new EvaluationDimensionScores(0, 0, 0, 0, 0, 0, 0, 0),
        new RetrievalScores(0, 0, 0, 0, 0), [], [], [], [], [], [], ["CaseExecutionFailed"],
        new EvaluationUsage(0, 0, null, null, null, null, null, 0, 0, 0, 0, 0));

    private static EvaluationUsage Aggregate(IEnumerable<EvaluationUsage> items)
    {
        var all = items.ToArray();
        return new EvaluationUsage(all.Sum(x => x.ModelCalls), all.Sum(x => x.ToolCalls),
            SumNullable(all.Select(x => x.InputTokens)), SumNullable(all.Select(x => x.OutputTokens)),
            SumNullable(all.Select(x => x.CachedInputTokens)), SumNullable(all.Select(x => x.ReasoningTokens)),
            SumNullable(all.Select(x => x.TotalTokens)), all.Sum(x => x.DurationMilliseconds),
            all.Sum(x => x.ModelLatencyMilliseconds), all.Sum(x => x.ToolLatencyMilliseconds),
            all.Sum(x => x.CheckpointOverheadMilliseconds), all.Sum(x => x.EvidencePayloadCharacters));
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var all = values.ToArray();
        return all.Any(value => value.HasValue) ? all.Sum(value => value ?? 0) : null;
    }

    private static double? Change(long? current, long baseline) => current is null || baseline == 0
        ? null : Math.Round(100d * (current.Value - baseline) / baseline, 2);
}
