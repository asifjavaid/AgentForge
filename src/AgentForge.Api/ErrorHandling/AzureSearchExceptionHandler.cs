using Azure;
using Azure.Identity;
using AgentForge.Infrastructure.AzureSearch;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AgentForge.Api.ErrorHandling;

public sealed class AzureSearchExceptionHandler(
    ILogger<AzureSearchExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var response = exception switch
        {
            AzureSearchAccessException access => (
                StatusCodes.Status503ServiceUnavailable,
                "Azure AI Search authorization failed",
                $"The Entra identity cannot perform {access.Operation}. Assign {access.RequiredRole} on the configured search service; no API-key fallback is configured."),
            AuthenticationFailedException => (
                StatusCodes.Status503ServiceUnavailable,
                "Azure AI Search authentication failed",
                "DefaultAzureCredential could not authenticate. Sign in with an authorized Entra identity; no API-key fallback is configured."),
            RequestFailedException { Status: 401 or 403 } => (
                StatusCodes.Status503ServiceUnavailable,
                "Azure AI Search authorization failed",
                "The Entra identity requires Search Service Contributor to create or validate the index and Search Index Data Contributor to upload and query documents."),
            RequestFailedException { Status: 404 } => (
                StatusCodes.Status503ServiceUnavailable,
                "Azure AI Search index unavailable",
                "The configured index was not found. Run the explicit knowledge indexing operation before querying."),
            RequestFailedException { Status: 429 } => (
                StatusCodes.Status503ServiceUnavailable,
                "Azure AI Search is busy",
                "The search service throttled the request. Try again later."),
            RequestFailedException => (
                StatusCodes.Status502BadGateway,
                "Azure AI Search failure",
                "Azure AI Search could not complete the request."),
            _ => default
        };

        if (response == default) return false;

        var requestFailure = exception as RequestFailedException ?? exception.InnerException as RequestFailedException;
        logger.LogWarning(
            "Azure AI Search operation failed. Status: {Status}; ErrorCode: {ErrorCode}; TraceIdentifier: {TraceIdentifier}",
            requestFailure?.Status,
            requestFailure?.ErrorCode,
            httpContext.TraceIdentifier);

        var problem = new ProblemDetails
        {
            Status = response.Item1,
            Title = response.Item2,
            Detail = response.Item3,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = response.Item1;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
