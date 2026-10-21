using AgentForge.Application.MultiAgent;

namespace AgentForge.Application.Evaluation;

public enum EvaluationCheckStatus
{
    Passed,
    Failed,
    NotExecuted
}

public enum EvaluationOutcome
{
    Pass,
    PassWithConcerns,
    Fail,
    NotExecuted
}

public enum EvaluationExecutionMode
{
    OfflineScripted,
    Live
}

public sealed record EvaluationDataset(
    string DatasetId,
    int Version,
    IReadOnlyList<EvaluationCase> Cases);

public sealed record EvaluationCase(
    string Id,
    string Objective,
    string Category,
    IReadOnlyList<AgentRole> RequiredRoles,
    IReadOnlyList<string> ExpectedEvidence,
    IReadOnlyList<string> RequiredAssertions,
    IReadOnlyList<string> ForbiddenAssertions,
    WorkflowStatus ExpectedOutcome,
    EvaluationSecurityExpectation Security,
    EvaluationBudget Budget,
    IReadOnlyList<string> Tags,
    ScriptedEvaluationObservation Fixture);

public sealed record EvaluationSecurityExpectation(
    bool RequireReviewer,
    bool RejectUnauthorizedTools,
    bool RejectPromptInjection,
    bool RejectUnsupportedCitations);

public sealed record EvaluationBudget(
    int MaxModelCalls,
    int MaxToolCalls,
    int MaxTotalTokens,
    int MaxRevisions);

public sealed record EvaluationClaim(
    string Assertion,
    bool Supported,
    IReadOnlyList<string> Evidence);

public sealed record EvaluationDependency(
    AgentRole Role,
    IReadOnlyList<AgentRole> DependsOn);

public sealed record EvaluationUsage(
    int ModelCalls,
    int ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    int? ReasoningTokens,
    int? TotalTokens,
    long DurationMilliseconds,
    long ModelLatencyMilliseconds,
    long ToolLatencyMilliseconds,
    long CheckpointOverheadMilliseconds,
    int EvidencePayloadCharacters);

public sealed record ScriptedEvaluationObservation(
    WorkflowStatus Status,
    IReadOnlyList<AgentRole> InvokedRoles,
    IReadOnlyList<EvaluationDependency> Dependencies,
    IReadOnlyList<EvaluationClaim> Claims,
    IReadOnlyList<string> RetrievedEvidence,
    IReadOnlyList<string> Citations,
    IReadOnlyList<string> SecurityViolations,
    int RevisionCount,
    int FailedTasks,
    int RetriedTasks,
    int DuplicateCommittedTasks,
    bool RecoverySucceeded,
    bool ReviewerExecuted,
    bool Abstained,
    string? FinalReport,
    EvaluationUsage Usage);

public sealed record EvaluationDimensionScores(
    double FactualAccuracy,
    double EvidenceQuality,
    double RequirementCoverage,
    double ArchitectureUsefulness,
    double SecurityReviewQuality,
    double WorkflowEfficiency,
    double Reliability,
    double QualityScore);

public sealed record RetrievalScores(
    double HitAtK,
    double MeanReciprocalRank,
    double RelevantEvidenceCoverage,
    double DuplicateEvidenceRate,
    double CitationValidity);

public sealed record EvaluationCaseResult(
    string CaseId,
    EvaluationOutcome Outcome,
    EvaluationDimensionScores Scores,
    RetrievalScores Retrieval,
    IReadOnlyList<string> MissingAssertions,
    IReadOnlyList<string> UnsupportedAssertions,
    IReadOnlyList<string> InvalidCitations,
    IReadOnlyList<AgentRole> MissingRoles,
    IReadOnlyList<AgentRole> UnnecessaryRoles,
    IReadOnlyList<string> SecurityViolations,
    IReadOnlyList<string> GateFailures,
    EvaluationUsage Usage);

public sealed record EvaluationRunResult(
    string DatasetId,
    int DatasetVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<EvaluationCaseResult> Cases,
    EvaluationOutcome Outcome,
    double AverageQualityScore,
    int Passed,
    int PassedWithConcerns,
    int Failed,
    int NotExecuted,
    EvaluationUsage AggregateUsage,
    BaselineComparison? BaselineComparison);

public sealed record EvaluationBaseline(
    string BaselineId,
    string Source,
    string Objective,
    int ModelCalls,
    int ToolCalls,
    int TotalTokens,
    long DurationMilliseconds,
    int Revisions,
    WorkflowStatus Status,
    bool SingleObservation);

public sealed record BaselineComparison(
    string BaselineId,
    bool Comparable,
    string Reason,
    double? TokenChangePercent,
    double? DurationChangePercent,
    int? ModelCallChange,
    int? ToolCallChange,
    int? RevisionChange);

public sealed record JudgeEvaluation(
    double ArchitectureUsefulness,
    double Clarity,
    double CitationRelevance,
    double ObjectiveCoverage,
    double UncertaintyCommunication,
    IReadOnlyList<string> Concerns);

public sealed record EvaluationCostEstimate(
    string Currency,
    decimal? Amount,
    string Status);

public sealed record EvaluationCostBreakdown(
    EvaluationCostEstimate Total,
    IReadOnlyDictionary<string, EvaluationCostEstimate> ByWorkflow,
    IReadOnlyDictionary<AgentRole, EvaluationCostEstimate> ByAgent,
    IReadOnlyDictionary<string, EvaluationCostEstimate> ByModel);

public sealed record ModelUsageRecord(
    string Provider,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    int? ReasoningTokens,
    int ToolCalls,
    int AttemptNumber,
    AgentRole Role,
    string WorkflowId,
    DateTimeOffset Timestamp);

public sealed record ModelPrice(
    string Provider,
    string Model,
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens,
    decimal? CachedInputPerMillionTokens,
    decimal? ReasoningPerMillionTokens);

public sealed record PricingCatalog(
    string Currency,
    IReadOnlyList<ModelPrice> Models);
