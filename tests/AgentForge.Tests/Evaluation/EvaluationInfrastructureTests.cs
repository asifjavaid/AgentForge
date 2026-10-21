using System.Text.Json;
using AgentForge.Application.Evaluation;
using AgentForge.Infrastructure.Evaluation;

namespace AgentForge.Tests.Evaluation;

public sealed class EvaluationInfrastructureTests
{
    [Fact]
    public async Task LoaderRejectsDuplicateCaseIdentifiers()
    {
        var dataset = await LoadAsync();
        var invalid = dataset with { Cases = [dataset.Cases[0], dataset.Cases[0]] };
        await AssertInvalidAsync(invalid);
    }

    [Fact]
    public async Task LoaderRejectsInvalidEvidenceIdentifier()
    {
        var dataset = await LoadAsync();
        var changed = dataset.Cases[0] with { ExpectedEvidence = ["C:\\machine-specific\\secret.txt"] };
        await AssertInvalidAsync(dataset with { Cases = [changed] });
    }

    [Fact]
    public async Task LoaderRejectsUnknownJsonMembers()
    {
        var source = await File.ReadAllTextAsync(EvaluationCorpusAndScoringTests.RepositoryFile(
            "evaluation", "datasets", "agentforge-engineering-v1.json"));
        var path = TemporaryFile(source.Replace("\"datasetId\"", "\"unexpected\": true, \"datasetId\"", StringComparison.Ordinal));
        try { await Assert.ThrowsAsync<InvalidDataException>(() => new JsonEvaluationDatasetLoader().LoadAsync(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoaderRejectsMalformedJson()
    {
        var path = TemporaryFile("{ not-json }");
        try { await Assert.ThrowsAsync<InvalidDataException>(() => new JsonEvaluationDatasetLoader().LoadAsync(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ResultStoreWritesJsonAndMarkdown()
    {
        var dataset = await LoadAsync();
        var result = await new EvaluationRunner(new ScriptedEvaluationCaseExecutor(), new EvaluationScoring(),
            TimeProvider.System).RunAsync(dataset with { Cases = [dataset.Cases[0]] });
        var directory = Path.Combine(Path.GetTempPath(), "agentforge-evaluation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var json = Path.Combine(directory, "result.json");
            var markdown = Path.Combine(directory, "result.md");
            await new FileEvaluationResultStore().SaveAsync(result, json, markdown);
            Assert.Contains("requirements-password-reset", await File.ReadAllTextAsync(json), StringComparison.Ordinal);
            Assert.Contains("# Evaluation run", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("--live")]
    [InlineData("--dataset")]
    public void CommandLineRejectsUnsafeOrIncompleteArguments(string argument) =>
        Assert.Throws<ArgumentException>(() => EvaluationRunOptions.Parse([argument]));

    [Fact]
    public void CommandLineRequiresExplicitLiveConfirmation()
    {
        var options = EvaluationRunOptions.Parse(["--live", "--confirm-live", "--api-base-url",
            "http://localhost:5065", "--case", "one", "--output", "out"]);
        Assert.True(options.Live);
        Assert.True(options.ConfirmLive);
        Assert.Equal("out", options.OutputDirectory);
        Assert.Equal("one", options.CaseId);
    }

    [Fact]
    public void VersionedSchemaIsPresent()
    {
        var schema = EvaluationCorpusAndScoringTests.RepositoryFile("evaluation", "schemas", "evaluation-case.schema.json");
        Assert.True(File.Exists(schema));
        Assert.Contains("requiredAssertions", File.ReadAllText(schema), StringComparison.Ordinal);
    }

    private static Task<EvaluationDataset> LoadAsync() =>
        new JsonEvaluationDatasetLoader().LoadAsync(EvaluationCorpusAndScoringTests.RepositoryFile(
            "evaluation", "datasets", "agentforge-engineering-v1.json"));

    private static async Task AssertInvalidAsync(EvaluationDataset dataset)
    {
        var path = TemporaryFile(JsonSerializer.Serialize(dataset, JsonEvaluationDatasetLoader.JsonOptions));
        try { await Assert.ThrowsAsync<InvalidDataException>(() => new JsonEvaluationDatasetLoader().LoadAsync(path)); }
        finally { File.Delete(path); }
    }

    private static string TemporaryFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentforge-eval-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
