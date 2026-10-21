using System.Security.Claims;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;

namespace AgentForge.Api.Authentication;

public static class ApprovalActorFactory
{
    public static ActorContext Create(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenant = principal.FindFirstValue(DevelopmentIdentityHandler.TenantClaim);
        var actorTypeValue = principal.FindFirstValue(DevelopmentIdentityHandler.ActorTypeClaim);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(tenant) ||
            !Enum.TryParse<ApprovalActorType>(actorTypeValue, out var actorType))
            throw new AgentOperationException(AgentFailureKind.AccessDenied,
                "The authenticated principal lacks controlled-action identity claims.");
        return new ActorContext(id, actorType, tenant,
            principal.FindAll(DevelopmentIdentityHandler.PermissionClaim)
                .Select(item => item.Value).ToHashSet(StringComparer.Ordinal));
    }
}
