using AgentForge.Api.Contracts;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController(IKnowledgeService knowledge) : ControllerBase
{
    [HttpPost("ask")]
    [ProducesResponseType<KnowledgeAnswer>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<KnowledgeAnswer>> AskAsync(
        [FromBody] AskKnowledgeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            ModelState.AddModelError(nameof(request.Question), "Question must contain non-whitespace characters.");
            return ValidationProblem(ModelState);
        }

        return Ok(await knowledge.AskAsync(request.Question, cancellationToken));
    }
}
