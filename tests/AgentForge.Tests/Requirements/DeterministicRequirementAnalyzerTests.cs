using AgentForge.Application.Requirements;
using AgentForge.Domain.Requirements;

namespace AgentForge.Tests.Requirements;

public sealed class DeterministicRequirementAnalyzerTests
{
    private readonly IRequirementAnalyzer _analyzer = new DeterministicRequirementAnalyzer();

    [Fact]
    public async Task AnalyzeAsync_ReturnsRealisticDeterministicAnalysis()
    {
        const string requirement =
            "Add forgot-password functionality. The user should receive an email containing a secure password-reset link.";

        var first = await _analyzer.AnalyzeAsync(requirement);
        var second = await _analyzer.AnalyzeAsync(requirement);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.FunctionalRequirements, second.FunctionalRequirements);
        Assert.Equal(first.NonFunctionalRequirements, second.NonFunctionalRequirements);
        Assert.Equal(first.TechnicalConsiderations, second.TechnicalConsiderations);
        Assert.Equal(first.SecurityRisks, second.SecurityRisks);
        Assert.Equal(first.Questions, second.Questions);
        Assert.Equal(first.Complexity, second.Complexity);
        Assert.Equal(RequirementComplexity.Medium, first.Complexity);
        Assert.Contains(first.FunctionalRequirements, item => item.Contains("single-use", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.SecurityRisks, item => item.Contains("enumeration", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(first.Questions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeAsync_RejectsEmptyRequirement(string requirement)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _analyzer.AnalyzeAsync(requirement));
    }

    [Fact]
    public async Task AnalyzeAsync_ObservesCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _analyzer.AnalyzeAsync("A valid requirement", cancellationTokenSource.Token));
    }
}
