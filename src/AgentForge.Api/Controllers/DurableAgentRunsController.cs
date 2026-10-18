using AgentForge.Api.Contracts;
using AgentForge.Application.DurableAgents;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/agent/runs")]
public sealed class DurableAgentRunsController(
    IDurableAgentRunManager manager,
    ILogger<DurableAgentRunsController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<DurableAgentRunView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DurableAgentRunView>> CreateAsync(
        [FromBody] AskAgentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            ModelState.AddModelError(nameof(request.Question), "Question must contain non-whitespace characters.");
            return ValidationProblem(ModelState);
        }
        var result = await manager.CreateAsync(request.Question, cancellationToken);
        logger.LogInformation("Durable run API created run. RunId: {RunId}; Status: {Status}", result.RunId, result.Status);
        return CreatedAtRoute("GetDurableAgentRun", new { runId = result.RunId }, result);
    }

    [HttpGet("{runId}", Name = "GetDurableAgentRun")]
    [ProducesResponseType<DurableAgentRunView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DurableAgentRunView>> GetAsync(
        string runId,
        CancellationToken cancellationToken) =>
        Ok(await manager.GetAsync(runId, cancellationToken));

    [HttpPost("{runId}/resume")]
    [ProducesResponseType<DurableAgentRunView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DurableAgentRunView>> ResumeAsync(
        string runId,
        CancellationToken cancellationToken) =>
        Ok(await manager.ResumeAsync(runId, cancellationToken));

    [HttpPost("{runId}/cancel")]
    [ProducesResponseType<DurableAgentRunView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DurableAgentRunView>> CancelAsync(
        string runId,
        CancellationToken cancellationToken) =>
        Ok(await manager.CancelAsync(runId, cancellationToken));
}
