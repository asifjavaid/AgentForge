using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using AgentForge.Infrastructure.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.Tests.Repositories;

public sealed class RepositoryToolsTests
{
    [Fact]
    public async Task ListFiles_SucceedsWithinRepository()
    {
        var fixture = CreateFixture();

        var result = await fixture.ListFiles.ExecuteAsync(
            fixture.Repository,
            """{"path":".","recursive":true}""");

        Assert.Contains("src/Auth/AuthService.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST_SECRET", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_ReturnsPermittedTextFile()
    {
        var fixture = CreateFixture();

        var result = await fixture.ReadFile.ExecuteAsync(
            fixture.Repository,
            """{"path":"src/Auth/AuthService.cs"}""");

        Assert.Contains("Authenticate", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":false", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCode_FindsKnownFixtureContent()
    {
        var fixture = CreateFixture();

        var result = await fixture.SearchCode.ExecuteAsync(
            fixture.Repository,
            """{"query":"Authenticate","path":"src"}""");

        Assert.Contains("src/Auth/AuthService.cs", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"line\":5", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_RejectsParentTraversal()
    {
        var fixture = CreateFixture();

        var exception = Assert.Throws<AgentOperationException>(() =>
            fixture.Boundary.ResolvePath(fixture.Repository, "../../outside.txt"));

        Assert.Equal(AgentFailureKind.AccessDenied, exception.FailureKind);
    }

    [Fact]
    public void ResolvePath_RejectsAbsolutePathOutsideAllowedRoot()
    {
        var fixture = CreateFixture();
        var outside = Path.GetFullPath(Path.Combine(fixture.AllowedRoot, "..", "outside.txt"));

        var exception = Assert.Throws<AgentOperationException>(() =>
            fixture.Boundary.ResolvePath(fixture.Repository, outside));

        Assert.Equal(AgentFailureKind.AccessDenied, exception.FailureKind);
    }

    [Fact]
    public async Task ReadFile_RejectsSensitiveFile()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            fixture.ReadFile.ExecuteAsync(fixture.Repository, """{"path":".env"}"""));

        Assert.Equal(AgentFailureKind.AccessDenied, exception.FailureKind);
    }

    [Fact]
    public async Task Dispatcher_RejectsUnknownTool()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            fixture.Dispatcher.DispatchAsync(
                fixture.Repository,
                new AgentToolCall("call-1", "delete_file", "{}")));

        Assert.Equal(AgentFailureKind.InvalidTool, exception.FailureKind);
    }

    [Fact]
    public async Task ReadFile_RejectsUnsupportedFile()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            fixture.ReadFile.ExecuteAsync(fixture.Repository, """{"path":"asset.bin"}"""));

        Assert.Equal(AgentFailureKind.AccessDenied, exception.FailureKind);
    }

    [Fact]
    public async Task ReadFile_TruncatesOversizedFile()
    {
        using var temporary = TemporaryRepository.Create();
        var largeFile = Path.Combine(temporary.RepositoryPath, "Large.txt");
        await File.WriteAllTextAsync(largeFile, new string('A', 2_000));
        var fixture = CreateFixture(temporary.AllowedRoot, maxFileBytes: 1_024);

        var result = await fixture.ReadFile.ExecuteAsync(
            fixture.Repository,
            """{"path":"Large.txt"}""");

        Assert.Contains("\"truncated\":true", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"returnedBytes\":1024", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 1_100), result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCode_BoundsResultCount()
    {
        using var temporary = TemporaryRepository.Create();
        await File.WriteAllTextAsync(
            Path.Combine(temporary.RepositoryPath, "Many.cs"),
            string.Join(Environment.NewLine, Enumerable.Repeat("match target", 10)));
        var fixture = CreateFixture(temporary.AllowedRoot, maxSearchResults: 2);

        var result = await fixture.SearchCode.ExecuteAsync(
            fixture.Repository,
            """{"query":"target","path":"."}""");

        Assert.Equal(2, CountOccurrences(result.Content, "\"path\":"));
        Assert.Contains("\"truncated\":true", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolArguments_RejectUnknownProperties()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            fixture.ReadFile.ExecuteAsync(
                fixture.Repository,
                """{"path":"README.md","command":"delete"}"""));

        Assert.Equal(AgentFailureKind.InvalidArguments, exception.FailureKind);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    internal static ToolFixture CreateFixture(
        string? allowedRoot = null,
        int maxFileBytes = 65_536,
        int maxSearchResults = 50)
    {
        allowedRoot ??= Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var options = Options.Create(new RepositoryAnalysisOptions
        {
            AllowedRoot = allowedRoot,
            MaxFileBytes = maxFileBytes,
            MaxSearchResults = maxSearchResults,
            MaxListResults = 200,
            MaxSearchFiles = 2_000,
            MaxToolResultCharacters = 100_000
        });
        var boundary = new RepositoryBoundary(options);
        var repository = boundary.ResolveRepository("SafeRepository");
        var list = new ListFilesTool(boundary, options);
        var read = new ReadFileTool(boundary, options);
        var search = new SearchCodeTool(boundary, options);
        var dispatcher = new RepositoryToolDispatcher(
            [list, read, search],
            options,
            NullLogger<RepositoryToolDispatcher>.Instance);
        return new ToolFixture(allowedRoot, boundary, repository, list, read, search, dispatcher);
    }

    internal sealed record ToolFixture(
        string AllowedRoot,
        RepositoryBoundary Boundary,
        RepositoryContext Repository,
        ListFilesTool ListFiles,
        ReadFileTool ReadFile,
        SearchCodeTool SearchCode,
        RepositoryToolDispatcher Dispatcher);

    private sealed class TemporaryRepository : IDisposable
    {
        private TemporaryRepository(string allowedRoot, string repositoryPath)
        {
            AllowedRoot = allowedRoot;
            RepositoryPath = repositoryPath;
        }

        public string AllowedRoot { get; }

        public string RepositoryPath { get; }

        public static TemporaryRepository Create()
        {
            var allowedRoot = Path.Combine(Path.GetTempPath(), $"agentforge-tests-{Guid.NewGuid():N}");
            var repository = Path.Combine(allowedRoot, "SafeRepository");
            Directory.CreateDirectory(repository);
            return new TemporaryRepository(allowedRoot, repository);
        }

        public void Dispose()
        {
            if (Directory.Exists(AllowedRoot))
            {
                Directory.Delete(AllowedRoot, recursive: true);
            }
        }
    }
}
