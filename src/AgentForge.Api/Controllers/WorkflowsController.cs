using AgentForge.Api.Contracts;
using AgentForge.Application.MultiAgent;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/workflows")]
public sealed class WorkflowsController(IMultiAgentWorkflowManager manager) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MultiAgentWorkflowView>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MultiAgentWorkflowView>> CreateAsync(
        [FromBody] CreateWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Objective))
        {
            ModelState.AddModelError(nameof(request.Objective), "Objective must contain non-whitespace characters.");
            return ValidationProblem(ModelState);
        }
        var workflow = await manager.CreateAsync(request.Objective, cancellationToken);
        return CreatedAtRoute("GetMultiAgentWorkflow", new { workflowId = workflow.WorkflowId }, workflow);
    }

    [HttpGet("{workflowId}", Name = "GetMultiAgentWorkflow")]
    public async Task<ActionResult<MultiAgentWorkflowView>> GetAsync(
        string workflowId,
        CancellationToken cancellationToken) => Ok(await manager.GetAsync(workflowId, cancellationToken));

    [HttpPost("{workflowId}/resume")]
    public async Task<ActionResult<MultiAgentWorkflowView>> ResumeAsync(
        string workflowId,
        CancellationToken cancellationToken) => Ok(await manager.ResumeAsync(workflowId, cancellationToken));

    [HttpPost("{workflowId}/cancel")]
    public async Task<ActionResult<MultiAgentWorkflowView>> CancelAsync(
        string workflowId,
        CancellationToken cancellationToken) => Ok(await manager.CancelAsync(workflowId, cancellationToken));

    [HttpGet("{workflowId}/report")]
    public async Task<ActionResult<object>> GetReportAsync(
        string workflowId,
        CancellationToken cancellationToken) => Ok(new
        {
            workflowId,
            report = await manager.GetReportAsync(workflowId, cancellationToken)
        });
}
