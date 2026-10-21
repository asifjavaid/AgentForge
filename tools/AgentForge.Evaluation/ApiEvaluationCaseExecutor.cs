using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Evaluation;
using AgentForge.Application.MultiAgent;

internal sealed class ApiEvaluationCaseExecutor(HttpClient httpClient) : IEvaluationCaseExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<ScriptedEvaluationObservation> ExecuteAsync(
        EvaluationCase evaluationCase,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var create = await httpClient.PostAsJsonAsync("api/workflows",
            new { objective = evaluationCase.Objective }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(create);
        var workflow = await create.Content.ReadFromJsonAsync<MultiAgentWorkflowView>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The workflow API returned an empty create response.");
        using var resume = await httpClient.PostAsync($"api/workflows/{workflow.WorkflowId}/resume", null, cancellationToken);
        await EnsureSuccessAsync(resume);
        workflow = await resume.Content.ReadFromJsonAsync<MultiAgentWorkflowView>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The workflow API returned an empty resume response.");
        started.Stop();

        var evidence = workflow.Tasks.SelectMany(item => item.Evidence)
            .Distinct(StringComparer.Ordinal).ToArray();
        var report = workflow.FinalReport;
        var citations = evidence.Where(item => report?.Contains(item, StringComparison.Ordinal) == true).ToArray();
        var roleByTask = workflow.Tasks.ToDictionary(item => item.TaskId, item => item.Role, StringComparer.Ordinal);
        var dependencies = workflow.Tasks.Where(item => item.DependsOn.Count > 0).Select(item =>
            new EvaluationDependency(item.Role, item.DependsOn.Where(roleByTask.ContainsKey)
                .Select(id => roleByTask[id]).Distinct().ToArray())).ToArray();
        var claims = evaluationCase.RequiredAssertions.Select(assertion => new EvaluationClaim(
            assertion,
            report?.Contains(assertion, StringComparison.OrdinalIgnoreCase) == true,
            citations)).ToArray();
        return new ScriptedEvaluationObservation(
            workflow.Status,
            workflow.Tasks.Select(item => item.Role).Distinct().ToArray(),
            dependencies,
            claims,
            evidence,
            citations,
            [],
            workflow.RevisionCount,
            workflow.Tasks.Count(item => item.Status == WorkflowTaskStatus.Failed),
            workflow.Tasks.Sum(item => Math.Max(0, item.AttemptCount - 1)),
            workflow.Tasks.GroupBy(item => item.TaskId, StringComparer.Ordinal).Count(group => group.Count() > 1),
            workflow.LastFailureClassification is null,
            workflow.Tasks.Any(item => item.Role == AgentRole.SecurityReviewer && item.Status == WorkflowTaskStatus.Completed),
            workflow.Status == WorkflowStatus.InsufficientEvidence,
            report,
            new EvaluationUsage(workflow.Usage.ModelCalls, workflow.Usage.ToolCalls,
                workflow.Usage.InputTokens, workflow.Usage.OutputTokens, null, null, workflow.Usage.TotalTokens,
                workflow.Usage.DurationMilliseconds, 0, 0, 0,
                evidence.Sum(item => item.Length)));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        await Task.CompletedTask;
        throw new HttpRequestException($"Workflow API returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
