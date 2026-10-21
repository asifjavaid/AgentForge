using AgentForge.Api.Authentication;
using AgentForge.Api.Contracts;
using AgentForge.Application.HumanApproval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class ApprovalsController(IHumanApprovalService service) : ControllerBase
{
    [HttpPost("action-proposals")]
    [Authorize(Policy = ApprovalPermissions.ProposalCreate)]
    [ProducesResponseType<ApprovalView>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ApprovalView>> CreateAsync(CreateActionProposalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(new AgentActionProposal(request.WorkflowId, request.TaskId,
            request.ActionType, request.TargetId, request.Parameters, request.Justification,
            request.EvidenceIds), ApprovalActorFactory.Create(User), HttpContext.TraceIdentifier, cancellationToken);
        return Created($"/api/approvals/{result.ApprovalId}", result);
    }

    [HttpGet("approvals")]
    [Authorize(Policy = ApprovalPermissions.Review)]
    public async Task<ActionResult<IReadOnlyList<ApprovalView>>> ListAsync([FromQuery] ApprovalStatus? status,
        CancellationToken cancellationToken) => Ok(await service.ListAsync(status,
            ApprovalActorFactory.Create(User), cancellationToken));

    [HttpGet("approvals/{approvalId}")]
    [Authorize(Policy = ApprovalPermissions.ProposalRead)]
    public async Task<ActionResult<ApprovalView>> GetAsync(string approvalId, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(approvalId, ApprovalActorFactory.Create(User), cancellationToken));

    [HttpPost("approvals/{approvalId}/approve")]
    [Authorize(Policy = ApprovalPermissions.Approve)]
    public async Task<ActionResult<ApprovalView>> ApproveAsync(string approvalId, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(approvalId, ApprovalActorFactory.Create(User),
            HttpContext.TraceIdentifier, cancellationToken));

    [HttpPost("approvals/{approvalId}/reject")]
    [Authorize(Policy = ApprovalPermissions.Reject)]
    public async Task<ActionResult<ApprovalView>> RejectAsync(string approvalId, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(approvalId, ApprovalActorFactory.Create(User),
            HttpContext.TraceIdentifier, cancellationToken));

    [HttpPost("approvals/{approvalId}/revoke")]
    [Authorize(Policy = ApprovalPermissions.Approve)]
    public async Task<ActionResult<ApprovalView>> RevokeAsync(string approvalId, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(approvalId, ApprovalActorFactory.Create(User),
            HttpContext.TraceIdentifier, cancellationToken));

    [HttpPost("approvals/{approvalId}/execute")]
    [Authorize(Policy = ApprovalPermissions.Execute)]
    public async Task<ActionResult<ApprovalView>> ExecuteAsync(string approvalId, CancellationToken cancellationToken) =>
        Ok(await service.ExecuteAsync(approvalId, ApprovalActorFactory.Create(User),
            HttpContext.TraceIdentifier, cancellationToken));

    [HttpGet("approvals/{approvalId}/audit")]
    [Authorize(Policy = ApprovalPermissions.AuditRead)]
    public async Task<ActionResult<IReadOnlyList<ApprovalAuditEvent>>> GetAuditAsync(string approvalId,
        CancellationToken cancellationToken) => Ok(await service.GetAuditAsync(approvalId,
            ApprovalActorFactory.Create(User), cancellationToken));
}
