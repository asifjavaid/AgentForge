using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentForge.Application.HumanApproval;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Authentication;

public sealed class DevelopmentIdentityOptions : AuthenticationSchemeOptions
{
    public bool Enabled { get; set; }
}

public sealed class DevelopmentIdentityHandler(
    IOptionsMonitor<DevelopmentIdentityOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<DevelopmentIdentityOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentForgeDevelopmentIdentity";
    public const string PermissionClaim = "agentforge_permission";
    public const string ActorTypeClaim = "agentforge_actor_type";
    public const string TenantClaim = "agentforge_tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Enabled) return Task.FromResult(AuthenticateResult.NoResult());
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = authorization[7..];
        var identity = token switch
        {
            "dev-agent-service" => Identity("agent-service", ApprovalActorType.Agent,
                ApprovalPermissions.ProposalCreate, ApprovalPermissions.ProposalRead),
            "dev-requester" => Identity("requester", ApprovalActorType.Human,
                ApprovalPermissions.ProposalCreate, ApprovalPermissions.ProposalRead),
            "dev-reviewer" => Identity("reviewer", ApprovalActorType.Human,
                ApprovalPermissions.ProposalRead, ApprovalPermissions.Review,
                ApprovalPermissions.Approve, ApprovalPermissions.Reject, ApprovalPermissions.AuditRead),
            "dev-executor" => Identity("executor", ApprovalActorType.Service,
                ApprovalPermissions.ProposalRead, ApprovalPermissions.Execute, ApprovalPermissions.AuditRead),
            "dev-auditor" => Identity("auditor", ApprovalActorType.Human,
                ApprovalPermissions.ProposalRead, ApprovalPermissions.Review, ApprovalPermissions.AuditRead),
            _ => null
        };
        if (identity is null) return Task.FromResult(AuthenticateResult.Fail("The development credential is invalid."));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), SchemeName)));
    }

    private static ClaimsIdentity Identity(string id, ApprovalActorType actorType, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, id),
            new(ActorTypeClaim, actorType.ToString()),
            new(TenantClaim, "local")
        };
        claims.AddRange(permissions.Select(item => new Claim(PermissionClaim, item)));
        return new ClaimsIdentity(claims, SchemeName);
    }
}
