using AgentForge.Application.Evaluation;
using AgentForge.Application.MultiAgent;
using AgentForge.Infrastructure.Evaluation;

namespace AgentForge.Tests.Evaluation;

public sealed class EvaluationCorpusAndScoringTests
{
    [Theory]
    [InlineData("requirements-password-reset")]
    [InlineData("repository-auth-implementation")]
    [InlineData("combined-requirements-code")]
    [InlineData("architecture-password-reset")]
    [InlineData("security-review-known-defects")]
    [InlineData("missing-documentation")]
    [InlineData("unknown-feature")]
    [InlineData("conflicting-requirements")]
    [InlineData("ambiguous-implementation")]
    [InlineData("knowledge-prompt-injection")]
    [InlineData("repository-prompt-injection")]
    [InlineData("cross-agent-injection")]
    [InlineData("dynamic-planning-narrow")]
    [InlineData("unnecessary-delegation")]
    [InlineData("parallel-specialists")]
    [InlineData("bounded-revision")]
    [InlineData("budget-exhaustion")]
    [InlineData("transient-provider-failure")]
    [InlineData("durable-recovery")]
    [InlineData("unsupported-citation")]
    [InlineData("tool-result-reuse")]
    [InlineData("observability-redaction")]
    [InlineData("assessment-08-matched-password-reset")]
    public async Task VersionedCorpusContainsRequiredCase(string caseId)
    {
        var dataset = await LoadAsync();
        Assert.Contains(dataset.Cases, item => item.Id == caseId);
        Assert.True(dataset.Cases.Count >= 20);
    }

    [Fact]
    public async Task CorpusFixturesPassDeterministicGates()
    {
        var dataset = await LoadAsync();
        var scoring = new EvaluationScoring();
        var results = dataset.Cases.Select(item => scoring.Score(item, item.Fixture)).ToArray();
        Assert.DoesNotContain(results, result => result.Outcome is EvaluationOutcome.Fail or EvaluationOutcome.NotExecuted);
        Assert.All(results, result => Assert.Empty(result.GateFailures));
    }

    [Fact]
    public async Task MissingRequiredAssertionIsReported()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Claims = [] });
        Assert.NotEmpty(result.MissingAssertions);
    }

    [Fact]
    public async Task FabricatedCitationTriggersSecurityGate()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var result = new EvaluationScoring().Score(testCase,
            testCase.Fixture with { Citations = ["repo:not-in-registry.cs"] });
        Assert.Contains("UnsupportedCitation", result.GateFailures);
    }

    [Fact]
    public async Task MissingCitationTriggersEvidenceGate()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Citations = [] });
        Assert.Contains("MissingCitation", result.GateFailures);
    }

    [Fact]
    public async Task ForbiddenSupportedAssertionIsReported()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var forbidden = Assert.Single(testCase.ForbiddenAssertions);
        var claims = testCase.Fixture.Claims.Append(new EvaluationClaim(forbidden, true, testCase.ExpectedEvidence)).ToArray();
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Claims = claims });
        Assert.Contains(forbidden, result.UnsupportedAssertions);
    }

    [Fact]
    public async Task MissingReviewerTriggersMandatoryGate()
    {
        var testCase = (await LoadAsync()).Cases.First(item => item.Security.RequireReviewer);
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { ReviewerExecuted = false });
        Assert.Contains("MandatoryReviewerMissing", result.GateFailures);
    }

    [Fact]
    public async Task IncorrectDependencyTriggersPlanGate()
    {
        var testCase = (await LoadAsync()).Cases.Single(item => item.Id == "parallel-specialists");
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Dependencies = [] });
        Assert.Contains(result.GateFailures, item => item.StartsWith("InvalidDependency:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnnecessaryRoleInvocationIsReported()
    {
        var testCase = (await LoadAsync()).Cases.Single(item => item.Id == "unnecessary-delegation");
        var roles = testCase.Fixture.InvokedRoles.Append(AgentRole.CodeAnalysis).ToArray();
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { InvokedRoles = roles });
        Assert.Contains(AgentRole.CodeAnalysis, result.UnnecessaryRoles);
    }

    [Fact]
    public async Task UnknownKnowledgeRequiresAbstention()
    {
        var testCase = (await LoadAsync()).Cases.Single(item => item.Id == "unknown-feature");
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Abstained = false });
        Assert.Contains("RequiredAbstentionMissing", result.GateFailures);
    }

    [Fact]
    public void RetrievalMetricsMeasureRankCoverageDuplicatesAndCitations()
    {
        var result = EvaluationScoring.Retrieval(["knowledge:a#chunk-1", "repo:b.cs"],
            ["synthetic:x", "repo:b.cs", "repo:b.cs"], ["repo:b.cs", "repo:missing.cs"]);
        Assert.Equal(1, result.HitAtK);
        Assert.Equal(0.5, result.MeanReciprocalRank);
        Assert.Equal(50, result.RelevantEvidenceCoverage);
        Assert.Equal(33.33, result.DuplicateEvidenceRate);
        Assert.Equal(50, result.CitationValidity);
    }

    [Fact]
    public async Task BudgetOverrunFailsGate()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var usage = testCase.Fixture.Usage with { ModelCalls = testCase.Budget.MaxModelCalls + 1 };
        var result = new EvaluationScoring().Score(testCase, testCase.Fixture with { Usage = usage });
        Assert.Contains("BudgetExceeded", result.GateFailures);
    }

    [Fact]
    public async Task RevisionOverrunFailsGate()
    {
        var testCase = (await LoadAsync()).Cases.Single(item => item.Id == "bounded-revision");
        var result = new EvaluationScoring().Score(testCase,
            testCase.Fixture with { RevisionCount = testCase.Budget.MaxRevisions + 1 });
        Assert.Contains("BudgetExceeded", result.GateFailures);
    }

    [Fact]
    public async Task SecurityViolationCannotBeHiddenByPerfectAverage()
    {
        var testCase = (await LoadAsync()).Cases[0];
        var result = new EvaluationScoring().Score(testCase,
            testCase.Fixture with { SecurityViolations = ["UnauthorizedToolAttempt"] });
        Assert.Equal(EvaluationOutcome.Fail, result.Outcome);
        Assert.Contains("Security:UnauthorizedToolAttempt", result.GateFailures);
    }

    private static Task<EvaluationDataset> LoadAsync() =>
        new JsonEvaluationDatasetLoader().LoadAsync(RepositoryFile("evaluation", "datasets", "agentforge-engineering-v1.json"));

    internal static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. segments]);
    }
}
