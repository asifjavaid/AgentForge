using AgentForge.Application.SoftwareAgents;
using AgentForge.Infrastructure.SoftwareAgents;
using System.Text.Json;

namespace AgentForge.Tests.SoftwareAgents;

public sealed class SoftwareAgentGuidanceTests
{
    [Fact]
    public void Prompt_PrefersDirectSearchOverDirectoryWalking()
    {
        Assert.Contains("Prefer search_code from the repository root", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("Do not walk the directory tree one level at a time", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("Search line context is repository evidence", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("request those reads together in one turn", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("read only the strongest candidates", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("no more than three diverse repository searches", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("return insufficient evidence rather than exhausting synonyms", SoftwareAgentPrompt.SystemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResultPresentation_BoundsAndDiversifiesFiles()
    {
        const string content = """
            {
              "matches": [
                { "path": "src/A.cs", "line": 1, "text": "a1" },
                { "path": "src/A.cs", "line": 2, "text": "a2" },
                { "path": "src/A.cs", "line": 3, "text": "a3" },
                { "path": "src/B.cs", "line": 1, "text": "b1" },
                { "path": "src/C.cs", "line": 1, "text": "c1" }
              ],
              "truncated": false,
              "scannedFiles": 20
            }
            """;

        var bounded = SoftwareAgentToolDispatcher.BoundSearchResult(content, 4);
        using var document = JsonDocument.Parse(bounded);
        var root = document.RootElement;
        var matches = root.GetProperty("matches").EnumerateArray().ToArray();

        Assert.Equal(4, matches.Length);
        Assert.Equal(
            ["src/A.cs", "src/B.cs", "src/C.cs", "src/A.cs"],
            matches.Select(match => match.GetProperty("path").GetString() ?? string.Empty).ToArray());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, root.GetProperty("availableMatches").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Options_RejectInvalidRepositorySearchPresentationLimit(int maximum)
    {
        var options = new SoftwareAgentOptions { MaxRepositorySearchResults = maximum };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Options_RejectInvalidRepositorySearchCallBudget(int maximum)
    {
        var options = new SoftwareAgentOptions { MaxRepositorySearchCalls = maximum };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
