using AgentForge.Api.Contracts;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController(
    IKnowledgeService knowledge,
    IServiceProvider services) : ControllerBase
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

        try
        {
            return Ok(await knowledge.AskAsync(request.Question, request.Source, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(nameof(request.Source), exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("index")]
    [ProducesResponseType<KnowledgeIndexingResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<KnowledgeIndexingResult>> IndexAsync(CancellationToken cancellationToken)
    {
        var manager = services.GetService<IKnowledgeIndexManager>();
        if (manager is null)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Managed knowledge indexing is not configured",
                Detail = "Select the AzureAiSearch knowledge retriever to initialize its managed index."
            });
        }

        return Ok(await manager.InitializeAndIndexAsync(cancellationToken));
    }
}
