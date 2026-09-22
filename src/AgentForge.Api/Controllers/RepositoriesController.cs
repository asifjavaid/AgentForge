using AgentForge.Api.Contracts;
using AgentForge.Application.Repositories;
using AgentForge.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/repositories")]
public sealed class RepositoriesController(
    IRepositoryAnalysisAgent agent,
    ILogger<RepositoriesController> logger) : ControllerBase
{
    [HttpPost("analyze")]
    [ProducesResponseType<RepositoryAnalysisRun>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RepositoryAnalysisRun>> AnalyzeAsync(
        [FromBody] AnalyzeRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryPath))
        {
            ModelState.AddModelError(nameof(request.RepositoryPath), "Repository path is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Request))
        {
            ModelState.AddModelError(nameof(request.Request), "Analysis request is required.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var result = await agent.AnalyzeAsync(
            request.RepositoryPath!,
            request.Request!,
            cancellationToken);

        logger.LogInformation(
            "Repository analysis completed. Provider: {Provider}; Model: {Model}; Iterations: {Iterations}; ToolCalls: {ToolCalls}; DurationMs: {DurationMs}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
            result.Metadata.Provider,
            result.Metadata.Model,
            result.Metadata.Iterations,
            result.Metadata.ToolCalls.Count,
            result.Metadata.DurationMilliseconds,
            result.Metadata.InputTokens,
            result.Metadata.OutputTokens,
            result.Metadata.TotalTokens);
        return Ok(result);
    }
}
