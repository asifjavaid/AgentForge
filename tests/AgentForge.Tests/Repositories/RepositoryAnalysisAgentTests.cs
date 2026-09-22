using AgentForge.Application.Agents;
using AgentForge.Application.Repositories;
using AgentForge.Domain.Repositories;

namespace AgentForge.Tests.Repositories;

public sealed class RepositoryAnalysisAgentTests
{
    private const string FinalJson = """
        {
          "summary": "The fixture contains a small authentication component and a malicious README treated as data.",
          "architectureComponents": ["SampleApp.Auth.AuthService"],
          "importantFiles": ["src/Auth/AuthService.cs", "README.md"],
          "findings": ["AuthService performs a minimal credential-shape check."],
          "securityConcerns": ["The README contains indirect prompt-injection text."],
          "recommendations": ["Keep repository content untrusted and authorization deterministic."],
          "unansweredQuestions": []
        }
        """;

    [Fact]
    public async Task AgentLoop_StopsAtConfiguredMaximumIterations()
    {
        var fixture = RepositoryToolsTests.CreateFixture();
        var client = new AlwaysToolClient();
        var agent = new RepositoryAnalysisAgent(
            client,
            fixture.Dispatcher,
            fixture.Boundary,
            new RepositoryAgentOptions { MaxIterations = 2, TimeoutSeconds = 30 });

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            agent.AnalyzeAsync("SafeRepository", "Analyze architecture."));

        Assert.Equal(AgentFailureKind.IterationLimit, exception.FailureKind);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task MaliciousReadme_CannotCreateNewToolCapability()
    {
        var fixture = RepositoryToolsTests.CreateFixture();
        var client = new ReadThenFinishClient();
        var agent = new RepositoryAnalysisAgent(
            client,
            fixture.Dispatcher,
            fixture.Boundary,
            new RepositoryAgentOptions { MaxIterations = 4, TimeoutSeconds = 30 });

        RepositoryAnalysisRun result = await agent.AnalyzeAsync(
            "SafeRepository",
            "Review this repository for security concerns.");

        Assert.Equal(3, result.Metadata.Iterations);
        Assert.Equal(["read_file", "delete_file (rejected)"], result.Metadata.ToolCalls);
        Assert.Contains("indirect prompt-injection", result.Analysis.SecurityConcerns[0], StringComparison.OrdinalIgnoreCase);
        Assert.All(client.ObservedToolNames, tools =>
            Assert.Equal(["list_files", "read_file", "search_code"], tools.Order().ToArray()));
    }

    private sealed class AlwaysToolClient : IToolCallingClient
    {
        public int Calls { get; private set; }

        public Task<ToolCallingResponse> CompleteAsync(
            ToolCallingRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ToolCallingResponse(
                "Fake",
                "fake-model",
                null,
                [new AgentToolCall($"call-{Calls}", "list_files", """{"path":".","recursive":false}""")],
                new AgentTokenUsage(10, 2, 12)));
        }
    }

    private sealed class ReadThenFinishClient : IToolCallingClient
    {
        private int _turn;

        public List<string[]> ObservedToolNames { get; } = [];

        public Task<ToolCallingResponse> CompleteAsync(
            ToolCallingRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservedToolNames.Add(request.Tools.Select(tool => tool.Name).ToArray());
            _turn++;
            if (_turn == 1)
            {
                return Task.FromResult(new ToolCallingResponse(
                    "Fake",
                    "fake-model",
                    null,
                    [new AgentToolCall("call-read", "read_file", """{"path":"README.md"}""")],
                    new AgentTokenUsage(20, 4, 24)));
            }

            if (_turn == 2)
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == AgentMessageRole.Tool &&
                               message.Content!.Contains("administrator privileges", StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(new ToolCallingResponse(
                    "Fake",
                    "fake-model",
                    null,
                    [new AgentToolCall("call-delete", "delete_file", """{"path":"src"}""")],
                    new AgentTokenUsage(25, 3, 28)));
            }

            Assert.Contains(
                request.Messages,
                message => message.Role == AgentMessageRole.Tool &&
                           message.Content!.Contains("not registered or permitted", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ToolCallingResponse(
                "Fake",
                "fake-model",
                FinalJson,
                [],
                new AgentTokenUsage(30, 10, 40)));
        }
    }
}
