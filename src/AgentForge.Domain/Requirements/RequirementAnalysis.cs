namespace AgentForge.Domain.Requirements;

public sealed record RequirementAnalysis(
    string Summary,
    IReadOnlyList<string> FunctionalRequirements,
    IReadOnlyList<string> NonFunctionalRequirements,
    IReadOnlyList<string> TechnicalConsiderations,
    IReadOnlyList<string> SecurityRisks,
    IReadOnlyList<string> Questions,
    RequirementComplexity Complexity);
