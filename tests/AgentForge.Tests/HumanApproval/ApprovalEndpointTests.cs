using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.HumanApproval;
using AgentForge.Application.MultiAgent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Tests.HumanApproval;

public sealed class ApprovalEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly WebApplicationFactory<Program> _factory;

    public ApprovalEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/api/action-proposals", "POST")]
    [InlineData("/api/approvals", "GET")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "GET")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approve", "POST")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reject", "POST")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/revoke", "POST")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/execute", "POST")]
    [InlineData("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/audit", "GET")]
    public async Task ApprovalEndpointsRejectUnauthenticatedRequests(string path, string method)
    {
        using var client = CreateClient(out _);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task InsufficientPermissionReturnsForbiddenWithoutMutation()
    {
        using var client = CreateClient(out var service);
        Authorize(client, "dev-auditor");
        var response = await client.PostAsync("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(service.LastActor);
    }

    [Fact]
    public async Task ProposalReaderCannotAccessAuditWithoutAuditPermission()
    {
        using var client = CreateClient(out var service);
        Authorize(client, "dev-requester");
        var response = await client.GetAsync("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/audit");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(service.LastActor);
    }

    [Fact]
    public async Task ApproverIdentityComesFromPrincipalNotRequestBody()
    {
        using var client = CreateClient(out var service);
        Authorize(client, "dev-reviewer");
        var response = await client.PostAsJsonAsync(
            "/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approve",
            new { approverId = "forged-admin" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("reviewer", service.LastActor!.ActorId);
        Assert.NotEqual("forged-admin", service.LastActor.ActorId);
    }

    [Fact]
    public async Task AgentServiceCanSubmitProposalButCannotApprove()
    {
        using var client = CreateClient(out _);
        Authorize(client, "dev-agent-service");
        var create = await client.PostAsJsonAsync("/api/action-proposals", ProposalRequest());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var approve = await client.PostAsync("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    [Fact]
    public async Task DevelopmentCredentialsCannotBeEnabledInProduction()
    {
        var service = new FakeApprovalService();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DevelopmentAuthentication:Enabled", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHumanApprovalService>();
                services.AddSingleton<IHumanApprovalService>(service);
            });
        });
        using var client = factory.CreateClient();
        Authorize(client, "dev-reviewer");
        var response = await client.PostAsync("/api/approvals/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approve", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerExposesApprovalLifecycleAlongsideExistingApis()
    {
        using var client = CreateClient(out _);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/workflows", out _));
        Assert.True(paths.TryGetProperty("/api/action-proposals", out _));
        Assert.True(paths.TryGetProperty("/api/approvals", out _));
        Assert.True(paths.TryGetProperty("/api/approvals/{approvalId}/approve", out _));
        Assert.True(paths.TryGetProperty("/api/approvals/{approvalId}/execute", out _));
        Assert.True(paths.TryGetProperty("/api/approvals/{approvalId}/audit", out _));
    }

    [Fact]
    public async Task FullWorkflowHandoffRequiresHumanDecisionBeforeControlledExecution()
    {
        using var client = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMultiAgentWorkflowManager>();
            services.AddSingleton<IMultiAgentWorkflowManager>(new CompletedWorkflowManager());
        })).CreateClient();
        Authorize(client, "dev-agent-service");
        var create = await client.PostAsJsonAsync("/api/action-proposals", ProposalRequest());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pending = (await create.Content.ReadFromJsonAsync<ApprovalView>(JsonOptions))!;
        Assert.Equal(ApprovalStatus.PendingApproval, pending.Status);

        Authorize(client, "dev-reviewer");
        var approved = await (await client.PostAsync($"/api/approvals/{pending.ApprovalId}/approve", null))
            .Content.ReadFromJsonAsync<ApprovalView>(JsonOptions);
        Assert.Equal(ApprovalStatus.Approved, approved!.Status);

        Authorize(client, "dev-executor");
        var executed = await (await client.PostAsync($"/api/approvals/{pending.ApprovalId}/execute", null))
            .Content.ReadFromJsonAsync<ApprovalView>(JsonOptions);
        Assert.Equal(ApprovalStatus.Executed, executed!.Status);
        Assert.NotNull(executed.Receipt);
    }

    private HttpClient CreateClient(out FakeApprovalService service)
    {
        service = new FakeApprovalService();
        var captured = service;
        return _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHumanApprovalService>();
            services.AddSingleton<IHumanApprovalService>(captured);
        })).CreateClient();
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static object ProposalRequest() => new
    {
        workflowId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        taskId = "report",
        actionType = "UpdateSandboxFeatureFlag",
        targetId = "sandbox-feature-flags",
        parameters = new { flagName = "enable-demo-feature", enabled = true },
        justification = "Reviewed change",
        evidenceIds = new[] { "knowledge:authentication.md#chunk-1" },
        approvalStatus = "Approved",
        approverId = "forged-admin"
    };

    private sealed class FakeApprovalService : IHumanApprovalService
    {
        public ActorContext? LastActor { get; private set; }

        public Task<ApprovalView> CreateAsync(AgentActionProposal proposal, ActorContext actor,
            string correlationId, CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(View());
        }

        public Task<IReadOnlyList<ApprovalView>> ListAsync(ApprovalStatus? status, ActorContext actor,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult<IReadOnlyList<ApprovalView>>([View()]);
        }

        public Task<ApprovalView> GetAsync(string approvalId, ActorContext actor,
            CancellationToken cancellationToken = default) => Return(actor);

        public Task<IReadOnlyList<ApprovalAuditEvent>> GetAuditAsync(string approvalId, ActorContext actor,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult<IReadOnlyList<ApprovalAuditEvent>>([]);
        }

        public Task<ApprovalView> ApproveAsync(string approvalId, ActorContext actor, string correlationId,
            CancellationToken cancellationToken = default) => Return(actor);
        public Task<ApprovalView> RejectAsync(string approvalId, ActorContext actor, string correlationId,
            CancellationToken cancellationToken = default) => Return(actor);
        public Task<ApprovalView> RevokeAsync(string approvalId, ActorContext actor, string correlationId,
            CancellationToken cancellationToken = default) => Return(actor);
        public Task<ApprovalView> ExecuteAsync(string approvalId, ActorContext actor, string correlationId,
            CancellationToken cancellationToken = default) => Return(actor);

        private Task<ApprovalView> Return(ActorContext actor)
        {
            LastActor = actor;
            return Task.FromResult(View());
        }

        private static ApprovalView View()
        {
            using var document = JsonDocument.Parse("{\"flagName\":\"enable-demo-feature\",\"enabled\":true}");
            return new ApprovalView("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "report", "UpdateSandboxFeatureFlag",
                "sandbox-feature-flags", document.RootElement.Clone(), "Reviewed change",
                ["knowledge:authentication.md#chunk-1"], ActionRisk.Medium, "A10-v1",
                "HumanApprovalRequired", "requester", "local", "LocalSandbox", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(30), new string('A', 64), ApprovalStatus.PendingApproval,
                null, null, null);
        }
    }

    private sealed class CompletedWorkflowManager : IMultiAgentWorkflowManager
    {
        public Task<MultiAgentWorkflowView> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View());
        public Task<MultiAgentWorkflowView> CreateAsync(string objective, CancellationToken cancellationToken = default) =>
            Task.FromResult(View());
        public Task<MultiAgentWorkflowView> ResumeAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View());
        public Task<MultiAgentWorkflowView> CancelAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(View());
        public Task<string> GetReportAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult("# reviewed");

        private static MultiAgentWorkflowView View() => new(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WorkflowStatus.Completed, "Completed",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0,
            [new WorkflowTaskView("report", AgentRole.ReportSynthesis, [], WorkflowTaskStatus.Completed,
                1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 0, 10, 1,
                ["knowledge:authentication.md#chunk-1"], null)],
            new WorkflowUsageView(1, 0, 1, 1, 2, 1), ReviewOutcome.Approved,
            WorkflowTerminationReason.Completed, "# reviewed", null);
    }
}
