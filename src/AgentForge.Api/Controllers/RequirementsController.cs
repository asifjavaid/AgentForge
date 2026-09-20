using AgentForge.Api.Contracts;
using AgentForge.Application.Requirements;
using AgentForge.Domain.Requirements;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/requirements")]
public sealed class RequirementsController(IRequirementAnalyzer requirementAnalyzer) : ControllerBase
{
    [HttpPost("analyze")]
    [ProducesResponseType<RequirementAnalysis>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RequirementAnalysis>> AnalyzeAsync(
        [FromBody] AnalyzeRequirementRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Requirement))
        {
            ModelState.AddModelError(
                nameof(request.Requirement),
                "Requirement must contain non-whitespace characters.");

            return ValidationProblem(ModelState);
        }

        var analysis = await requirementAnalyzer.AnalyzeAsync(
            request.Requirement,
            cancellationToken);

        return Ok(analysis);
    }
}
