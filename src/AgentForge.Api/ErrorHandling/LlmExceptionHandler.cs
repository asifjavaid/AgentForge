using AgentForge.Application.Llm;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.ErrorHandling;

public sealed class LlmExceptionHandler(ILogger<LlmExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not LlmOperationException llmException)
        {
            return false;
        }

        var (statusCode, title, detail) = llmException.FailureKind switch
        {
            LlmFailureKind.Configuration or LlmFailureKind.Authentication =>
                (StatusCodes.Status503ServiceUnavailable, "LLM service unavailable", "The requirement analysis service is not configured or available."),
            LlmFailureKind.RateLimit =>
                (StatusCodes.Status503ServiceUnavailable, "LLM service busy", "The requirement analysis service is temporarily busy. Try again later."),
            LlmFailureKind.Timeout =>
                (StatusCodes.Status504GatewayTimeout, "LLM service timeout", "The requirement analysis service did not respond in time."),
            LlmFailureKind.InvalidResponse =>
                (StatusCodes.Status502BadGateway, "Invalid LLM response", "The requirement analysis service returned an invalid response."),
            _ =>
                (StatusCodes.Status502BadGateway, "LLM provider failure", "The requirement analysis service could not complete the request.")
        };

        logger.LogWarning(
            "LLM operation failed. FailureKind: {FailureKind}; TraceIdentifier: {TraceIdentifier}",
            llmException.FailureKind,
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
