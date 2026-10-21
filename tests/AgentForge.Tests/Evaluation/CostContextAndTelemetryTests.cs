using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Application.Evaluation;
using AgentForge.Application.MultiAgent;
using AgentForge.Application.Observability;
using Xunit.Abstractions;

namespace AgentForge.Tests.Evaluation;

public sealed class CostContextAndTelemetryTests(ITestOutputHelper testOutput)
{
    [Fact]
    public void CostUsesConfiguredProviderModelPricing()
    {
        var accounting = new CostAccounting(new PricingCatalog("USD",
            [new ModelPrice("AzureFoundry", "configured-model", 2m, 8m, 0.5m, 10m)]));
        var result = accounting.Estimate([Usage("AzureFoundry", "configured-model", 1_000_000, 100_000, 200_000, 50_000)]);
        Assert.Equal(3m, result.Amount);
        Assert.Equal("EstimatedNotInvoice", result.Status);
    }

    [Theory]
    [InlineData("other-provider", "configured-model")]
    [InlineData("AzureFoundry", "other-model")]
    public void UnknownPricingIsExplicitlyUnavailable(string provider, string model)
    {
        var accounting = new CostAccounting(new PricingCatalog("USD",
            [new ModelPrice("AzureFoundry", "configured-model", 1, 1, null, null)]));
        var result = accounting.Estimate([Usage(provider, model, 1, 1, null, null)]);
        Assert.Null(result.Amount);
        Assert.Equal("CostEstimateUnavailable", result.Status);
    }

    [Fact]
    public void MissingTokenUsageIsExplicitlyUnavailable()
    {
        var accounting = new CostAccounting(new PricingCatalog("USD",
            [new ModelPrice("p", "m", 1, 1, null, null)]));
        Assert.Equal("CostEstimateUnavailable", accounting.Estimate([Usage("p", "m", null, 1, null, null)]).Status);
    }

    [Fact]
    public void UnsupportedCachedOrReasoningRatesAreNotGuessed()
    {
        var accounting = new CostAccounting(new PricingCatalog("USD",
            [new ModelPrice("p", "m", 1, 1, null, null)]));
        Assert.Equal("CostEstimateUnavailable", accounting.Estimate([Usage("p", "m", 10, 10, 1, null)]).Status);
        Assert.Equal("CostEstimateUnavailable", accounting.Estimate([Usage("p", "m", 10, 10, null, 1)]).Status);
    }

    [Fact]
    public void CostBreakdownAggregatesByWorkflowAgentAndProviderModel()
    {
        var accounting = new CostAccounting(new PricingCatalog("USD", [new ModelPrice("p", "m", 1, 1, null, null)]));
        var first = Usage("p", "m", 100, 100, null, null);
        var second = first with { WorkflowId = "other", Role = AgentRole.RequirementsAnalyst };
        var result = accounting.EstimateBreakdown([first, second]);
        Assert.Equal(2, result.ByWorkflow.Count);
        Assert.Equal(2, result.ByAgent.Count);
        Assert.Single(result.ByModel);
        Assert.NotNull(result.Total.Amount);
    }

    [Fact]
    public void OptimizedContextIsSmallerAndDeduplicatesEvidence()
    {
        var agentOutput = Output(["knowledge:a#chunk-1", "knowledge:a#chunk-1"]);
        var dependency = Task("requirements", AgentRole.RequirementsAnalyst, agentOutput);
        var context = Context([dependency], ["knowledge:a#chunk-1", "repo:unrelated.cs"]);
        var result = WorkflowContextBuilder.Build(Task("architect", AgentRole.SolutionArchitect), context);
        testOutput.WriteLine("Context characters: before={0}; after={1}; reduction={2:F2}%",
            result.OriginalCharacters, result.OptimizedCharacters,
            100d * (result.OriginalCharacters - result.OptimizedCharacters) / result.OriginalCharacters);
        Assert.True(result.OptimizedCharacters < result.OriginalCharacters);
        Assert.Equal(["knowledge:a#chunk-1"], result.Evidence);
        Assert.DoesNotContain("repo:unrelated.cs", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceProducingAgentRetainsAuthorizedRegistry()
    {
        var result = WorkflowContextBuilder.Build(Task("code", AgentRole.CodeAnalysis),
            Context([], ["repo:a.cs", "repo:a.cs", "knowledge:b#chunk-1"]));
        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains("repo:a.cs", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedContextFailsClosedWithoutBlindTruncation()
    {
        var oversized = new string('x', WorkflowContextBuilder.MaximumContextCharacters + 1);
        var context = new AgentExecutionContext("workflow", "correlation", oversized, [], [], 1, 0, 1, 0);
        var exception = Assert.Throws<AgentOperationException>(() =>
            WorkflowContextBuilder.Build(Task("architect", AgentRole.SolutionArchitect), context));
        Assert.Equal(AgentFailureKind.InvalidRequest, exception.FailureKind);
    }

    [Fact]
    public void TelemetryActivityContainsSafeMetadataOnly()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentForgeTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        const string secret = "do-not-record-this-secret";
        using (AgentForgeTelemetry.Start("agent.test", "workflow-id", "task-id", "CodeAnalysis")) { }
        Assert.NotNull(captured);
        var serializedTags = string.Join('|', captured.Tags.Select(item => $"{item.Key}={item.Value}"));
        Assert.Contains("workflow-id", serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, serializedTags, StringComparison.Ordinal);
    }

    private static ModelUsageRecord Usage(string provider, string model, int? input, int? output,
        int? cached, int? reasoning) => new(provider, model, input, output, cached, reasoning,
        0, 1, AgentRole.CodeAnalysis, "workflow", DateTimeOffset.UtcNow);

    private static WorkflowTaskState Task(string id, AgentRole role, AgentTaskOutput? output = null) => new(
        id, role, "A deliberately long objective used to make context compaction measurable.", [],
        output is null ? WorkflowTaskStatus.Running : WorkflowTaskStatus.Completed, 1,
        DateTimeOffset.UtcNow, output is null ? null : DateTimeOffset.UtcNow, output, [], null, null);

    private static AgentTaskOutput Output(IReadOnlyList<string> evidence) => new(
        "summary", ["finding"], [], [], [], evidence, false, ReviewOutcome.NotApplicable, null, null, null);

    private static AgentExecutionContext Context(IReadOnlyList<WorkflowTaskState> dependencies,
        IReadOnlyList<string> evidence) => new("workflow", "correlation", "objective", dependencies,
        evidence, 4, 3, 40_000, 0);
}
