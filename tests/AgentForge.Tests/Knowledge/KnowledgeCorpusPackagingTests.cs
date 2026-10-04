namespace AgentForge.Tests.Knowledge;

public sealed class KnowledgeCorpusPackagingTests
{
    [Fact]
    public void BuildOutput_ContainsControlledKnowledgeCorpus()
    {
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "knowledge");

        Assert.True(
            Directory.Exists(corpusPath),
            $"Expected the knowledge corpus at '{corpusPath}'.");

        var fileNames = Directory
            .EnumerateFiles(corpusPath, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "api-spec.md",
                "architecture.md",
                "authentication.md",
                "deployment.md",
                "security-guidelines.md"
            ],
            fileNames);
    }
}
