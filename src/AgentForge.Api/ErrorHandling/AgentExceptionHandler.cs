using AgentForge.Application.Agents;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.ErrorHandling;

public sealed class AgentExceptionHandler(ILogger<AgentExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AgentOperationException agentException)
        {
            return false;
        }

        var (statusCode, title, detail) = agentException.FailureKind switch
        {
            AgentFailureKind.InvalidRequest or AgentFailureKind.InvalidArguments =>
                (StatusCodes.Status400BadRequest, "Invalid repository analysis request", "The repository analysis request or tool arguments are invalid."),
            AgentFailureKind.AccessDenied =>
                (StatusCodes.Status403Forbidden, "Repository access denied", "The requested repository access is outside the permitted read-only boundary."),
            AgentFailureKind.Timeout =>
                (StatusCodes.Status504GatewayTimeout, "Repository analysis timeout", "Repository analysis did not finish within the configured time limit."),
            AgentFailureKind.IterationLimit =>
                (StatusCodes.Status502BadGateway, "Agent iteration limit reached", "Repository analysis stopped at the configured iteration limit."),
            AgentFailureKind.InvalidTool =>
                (StatusCodes.Status502BadGateway, "Invalid agent tool request", "The model requested a tool that the application does not permit."),
            AgentFailureKind.InvalidResponse =>
                (StatusCodes.Status502BadGateway, "Invalid agent response", "The model returned an invalid repository analysis response."),
            _ =>
                (StatusCodes.Status502BadGateway, "Repository analysis failure", "The repository analysis agent could not complete the request.")
        };

        logger.LogWarning(
            "Repository analysis failed. FailureKind: {FailureKind}; TraceIdentifier: {TraceIdentifier}",
            agentException.FailureKind,
            httpContext.TraceIdentifier);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }
}
