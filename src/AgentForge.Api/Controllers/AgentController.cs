using AgentForge.Api.Contracts;
using AgentForge.Application.SoftwareAgents;
using AgentForge.Domain.SoftwareAgents;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentController(
    ISoftwareEngineeringAgent agent,
    ILogger<AgentController> logger) : ControllerBase
{
    [HttpPost("ask")]
    [ProducesResponseType<SoftwareAgentRun>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SoftwareAgentRun>> AskAsync(
        [FromBody] AskAgentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            ModelState.AddModelError(nameof(request.Question), "Question must contain non-whitespace characters.");
            return ValidationProblem(ModelState);
        }

        var result = await agent.AskAsync(request.Question, cancellationToken);
        logger.LogInformation(
            "Software agent completed. RunId: {RunId}; TerminationReason: {TerminationReason}; ModelTurns: {ModelTurns}; ToolCalls: {ToolCalls}; Tools: {Tools}; DurationMs: {DurationMs}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
            result.RunId,
            result.TerminationReason,
            result.ModelTurns,
            result.ToolCallCount,
            result.ToolsUsed,
            result.DurationMilliseconds,
            result.Usage.InputTokens,
            result.Usage.OutputTokens,
            result.Usage.TotalTokens);
        return Ok(result);
    }
}
