using AgentForge.Application.Knowledge;
using AgentForge.Infrastructure.Knowledge;

namespace AgentForge.Tests.Knowledge;

public sealed class DocumentLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-{Guid.NewGuid():N}");

    public DocumentLoaderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Loader_LoadsMarkdownAndTextButIgnoresBinaryExtension()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "architecture.md"), "# Architecture");
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "Notes");
        await File.WriteAllBytesAsync(Path.Combine(_directory, "asset.bin"), [0, 1, 2]);

        var documents = await new MarkdownKnowledgeDocumentLoader(Options()).LoadAsync();

        Assert.Equal(2, documents.Count);
        Assert.Equal(["architecture.md", "notes.txt"], documents.Select(x => x.Source));
    }

    [Fact]
    public async Task Loader_RejectsOversizedDocument()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "large.md"), new string('x', 1100));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MarkdownKnowledgeDocumentLoader(Options(1024)).LoadAsync());
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private RagOptions Options(int maxBytes = 4096) => new() { CorpusPath = _directory, MaxDocumentBytes = maxBytes };
}
