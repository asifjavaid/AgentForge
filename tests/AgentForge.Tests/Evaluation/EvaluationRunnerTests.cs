using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Evaluation;

namespace AgentForge.Tests.Evaluation;

public sealed class EvaluationRunnerTests
{
    [Fact]
    public async Task RunnerAggregatesOfflineCorpusAndMatchedBaseline()
    {
        var dataset = await LoadAsync();
        var baseline = await LoadBaselineAsync();
        var result = await new EvaluationRunner(new ScriptedEvaluationCaseExecutor(), new EvaluationScoring(),
            TimeProvider.System).RunAsync(dataset, baseline);
        Assert.Equal(dataset.Cases.Count, result.Cases.Count);
        Assert.NotNull(result.BaselineComparison);
        Assert.False(result.BaselineComparison.Comparable);
        Assert.Null(result.BaselineComparison.ModelCallChange);
        Assert.Contains("scripted offline", result.BaselineComparison.Reason, StringComparison.Ordinal);

        var matched = EvaluationRunner.Compare(baseline, dataset.Cases[^1].Objective,
            new EvaluationScoring().Score(dataset.Cases[^1], dataset.Cases[^1].Fixture));
        Assert.True(matched.Comparable);
        Assert.Equal(-7, matched.ModelCallChange);
        Assert.Equal(-4, matched.ToolCallChange);
        Assert.Equal(-49.74, matched.TokenChangePercent);
    }

    [Fact]
    public async Task RunnerContainsIndividualCaseFailure()
    {
        var dataset = await LoadAsync();
        var subset = dataset with { Cases = dataset.Cases.Take(2).ToArray() };
        var result = await new EvaluationRunner(new FailingExecutor(subset.Cases[0].Id),
            new EvaluationScoring(), TimeProvider.System).RunAsync(subset);
        Assert.Equal(1, result.NotExecuted);
        Assert.Equal(EvaluationOutcome.NotExecuted, result.Outcome);
        Assert.Equal(EvaluationOutcome.Pass, result.Cases[1].Outcome);
    }

    [Fact]
    public async Task BaselineComparisonRejectsDifferentObjective()
    {
        var testCase = (await LoadAsync()).Cases[^1];
        var scored = new EvaluationScoring().Score(testCase, testCase.Fixture);
        var comparison = EvaluationRunner.Compare((await LoadBaselineAsync()) with { Objective = "different" },
            testCase.Objective, scored);
        Assert.False(comparison.Comparable);
        Assert.Null(comparison.TokenChangePercent);
    }

    [Fact]
    public async Task AggregatePreservesUnknownTokenUsage()
    {
        var dataset = await LoadAsync();
        var one = dataset.Cases[0];
        var fixture = one.Fixture with { Usage = one.Fixture.Usage with { TotalTokens = null } };
        var result = await new EvaluationRunner(new ScriptedEvaluationCaseExecutor(), new EvaluationScoring(),
            TimeProvider.System).RunAsync(dataset with { Cases = [one with { Fixture = fixture }] });
        Assert.Null(result.AggregateUsage.TotalTokens);
    }

    private static Task<EvaluationDataset> LoadAsync() =>
        new AgentForge.Infrastructure.Evaluation.JsonEvaluationDatasetLoader().LoadAsync(
            EvaluationCorpusAndScoringTests.RepositoryFile("evaluation", "datasets", "agentforge-engineering-v1.json"));

    private static async Task<EvaluationBaseline> LoadBaselineAsync()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        return JsonSerializer.Deserialize<EvaluationBaseline>(await File.ReadAllTextAsync(
            EvaluationCorpusAndScoringTests.RepositoryFile("evaluation", "baselines", "assessment-08-baseline.json")), options)!;
    }

    private sealed class FailingExecutor(string failedId) : IEvaluationCaseExecutor
    {
        public Task<ScriptedEvaluationObservation> ExecuteAsync(EvaluationCase evaluationCase,
            CancellationToken cancellationToken = default) => evaluationCase.Id == failedId
                ? throw new InvalidOperationException("Synthetic case failure")
                : Task.FromResult(evaluationCase.Fixture);
    }
}
