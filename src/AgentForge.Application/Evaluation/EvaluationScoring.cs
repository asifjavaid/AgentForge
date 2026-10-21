using AgentForge.Application.MultiAgent;

namespace AgentForge.Application.Evaluation;

public sealed class EvaluationScoring
{
    public EvaluationCaseResult Score(EvaluationCase testCase, ScriptedEvaluationObservation observation)
    {
        var supported = observation.Claims.Where(claim => claim.Supported)
            .Select(claim => claim.Assertion).ToHashSet(StringComparer.Ordinal);
        var missing = testCase.RequiredAssertions.Where(required => !supported.Contains(required)).ToArray();
        var unsupported = observation.Claims.Where(claim => !claim.Supported ||
                testCase.ForbiddenAssertions.Contains(claim.Assertion, StringComparer.Ordinal))
            .Select(claim => claim.Assertion).Distinct(StringComparer.Ordinal).ToArray();
        var evidence = observation.RetrievedEvidence.ToHashSet(StringComparer.Ordinal);
        var invalidCitations = observation.Citations.Where(citation => !evidence.Contains(citation))
            .Distinct(StringComparer.Ordinal).ToArray();
        var missingRoles = testCase.RequiredRoles.Except(observation.InvokedRoles).ToArray();
        var unnecessary = observation.InvokedRoles.Except(testCase.RequiredRoles)
            .Where(role => role != AgentRole.Supervisor).ToArray();
        var retrieval = Retrieval(testCase.ExpectedEvidence, observation.RetrievedEvidence, observation.Citations);
        var dependencyErrors = DependencyErrors(testCase, observation).ToArray();
        var gateFailures = Gates(testCase, observation, missingRoles, invalidCitations, dependencyErrors).ToArray();

        var coverage = Percent(testCase.RequiredAssertions.Count - missing.Length, testCase.RequiredAssertions.Count);
        var factual = observation.Claims.Count == 0
            ? (testCase.RequiredAssertions.Count == 0 ? 100 : 0)
            : Percent(observation.Claims.Count - unsupported.Length, observation.Claims.Count);
        var architecture = testCase.RequiredRoles.Contains(AgentRole.SolutionArchitect)
            ? (missingRoles.Contains(AgentRole.SolutionArchitect) ? 0 : coverage)
            : 100;
        var security = testCase.Security.RequireReviewer
            ? observation.ReviewerExecuted && observation.SecurityViolations.Count == 0 ? 100 : 0
            : observation.SecurityViolations.Count == 0 ? 100 : 0;
        var withinBudget = observation.Usage.ModelCalls <= testCase.Budget.MaxModelCalls &&
            observation.Usage.ToolCalls <= testCase.Budget.MaxToolCalls &&
            (observation.Usage.TotalTokens ?? 0) <= testCase.Budget.MaxTotalTokens &&
            observation.RevisionCount <= testCase.Budget.MaxRevisions;
        var efficiency = withinBudget ? 100 : 0;
        var reliability = observation.FailedTasks == 0 && observation.DuplicateCommittedTasks == 0 ? 100 : 75;
        var evidenceScore = (retrieval.RelevantEvidenceCoverage + retrieval.CitationValidity) / 2;
        var quality = (factual * 0.25) + (evidenceScore * 0.20) + (coverage * 0.15) +
            (architecture * 0.10) + (security * 0.15) + (efficiency * 0.05) + (reliability * 0.10);
        var scores = new EvaluationDimensionScores(factual, evidenceScore, coverage, architecture,
            security, efficiency, reliability, Math.Round(quality, 2));
        var outcome = gateFailures.Length > 0 || quality < 70
            ? EvaluationOutcome.Fail
            : unsupported.Length > 0 || missing.Length > 0 || observation.Status is WorkflowStatus.CompletedWithConcerns or WorkflowStatus.InsufficientEvidence
                ? EvaluationOutcome.PassWithConcerns
                : EvaluationOutcome.Pass;
        return new EvaluationCaseResult(testCase.Id, outcome, scores, retrieval, missing, unsupported,
            invalidCitations, missingRoles, unnecessary, observation.SecurityViolations, gateFailures, observation.Usage);
    }

    public static RetrievalScores Retrieval(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> retrieved,
        IReadOnlyList<string> citations)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var distinct = retrieved.Distinct(StringComparer.Ordinal).ToArray();
        var hits = distinct.Count(expectedSet.Contains);
        var firstRank = Array.FindIndex(distinct, expectedSet.Contains);
        var hitAtK = expected.Count == 0 ? 1 : hits > 0 ? 1 : 0;
        var mrr = expected.Count == 0 ? 1 : firstRank < 0 ? 0 : 1d / (firstRank + 1);
        var coverage = expected.Count == 0 ? 100 : Percent(hits, expectedSet.Count);
        var duplicates = retrieved.Count == 0 ? 0 :
            Math.Round(100d * (retrieved.Count - distinct.Length) / retrieved.Count, 2);
        var validCitations = citations.Count(citation => distinct.Contains(citation, StringComparer.Ordinal));
        var validity = citations.Count == 0 ? (expected.Count == 0 ? 100 : 0) : Percent(validCitations, citations.Count);
        return new RetrievalScores(hitAtK, Math.Round(mrr, 4), coverage, duplicates, validity);
    }

    private static IEnumerable<string> Gates(
        EvaluationCase testCase,
        ScriptedEvaluationObservation observation,
        IReadOnlyList<AgentRole> missingRoles,
        IReadOnlyList<string> invalidCitations,
        IReadOnlyList<string> dependencyErrors)
    {
        foreach (var violation in observation.SecurityViolations)
            yield return $"Security:{violation}";
        if (testCase.Security.RequireReviewer && !observation.ReviewerExecuted)
            yield return "MandatoryReviewerMissing";
        if (testCase.Security.RejectUnsupportedCitations && invalidCitations.Count > 0)
            yield return "UnsupportedCitation";
        if (testCase.ExpectedEvidence.Count > 0 && observation.Citations.Count == 0)
            yield return "MissingCitation";
        if (missingRoles.Count > 0)
            yield return "RequiredRoleMissing";
        foreach (var error in dependencyErrors)
            yield return error;
        if (testCase.ExpectedOutcome == WorkflowStatus.InsufficientEvidence && !observation.Abstained)
            yield return "RequiredAbstentionMissing";
        if (observation.DuplicateCommittedTasks > 0)
            yield return "DuplicateCommittedTask";
        if (observation.Status != testCase.ExpectedOutcome)
            yield return "UnexpectedOutcome";
        if (observation.Usage.ModelCalls > testCase.Budget.MaxModelCalls ||
            observation.Usage.ToolCalls > testCase.Budget.MaxToolCalls ||
            (observation.Usage.TotalTokens ?? 0) > testCase.Budget.MaxTotalTokens ||
            observation.RevisionCount > testCase.Budget.MaxRevisions)
            yield return "BudgetExceeded";
    }

    private static IEnumerable<string> DependencyErrors(
        EvaluationCase testCase,
        ScriptedEvaluationObservation observation)
    {
        var dependencies = observation.Dependencies.ToDictionary(item => item.Role, item => item.DependsOn);
        foreach (var role in testCase.RequiredRoles)
        {
            var expected = role switch
            {
                AgentRole.SolutionArchitect => testCase.RequiredRoles
                    .Where(candidate => candidate is AgentRole.RequirementsAnalyst or AgentRole.CodeAnalysis),
                AgentRole.SecurityReviewer when testCase.RequiredRoles.Contains(AgentRole.SolutionArchitect) =>
                    [AgentRole.SolutionArchitect],
                AgentRole.ReportSynthesis when testCase.RequiredRoles.Contains(AgentRole.SecurityReviewer) =>
                    [AgentRole.SecurityReviewer],
                _ => []
            };
            var expectedArray = expected.ToArray();
            if (expectedArray.Length == 0) continue;
            if (!dependencies.TryGetValue(role, out var actual) || expectedArray.Except(actual).Any())
                yield return $"InvalidDependency:{role}";
        }
    }

    private static double Percent(int numerator, int denominator) =>
        denominator == 0 ? 100 : Math.Round(100d * Math.Max(0, numerator) / denominator, 2);
}
