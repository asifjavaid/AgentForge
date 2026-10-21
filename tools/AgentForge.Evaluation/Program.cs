using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Evaluation;
using AgentForge.Infrastructure.Evaluation;

try
{
    var options = EvaluationRunOptions.Parse(args);
    var loader = new JsonEvaluationDatasetLoader();
    var dataset = await loader.LoadAsync(options.DatasetPath);
    if (options.CaseId is not null)
    {
        var selected = dataset.Cases.SingleOrDefault(item => item.Id == options.CaseId)
            ?? throw new ArgumentException($"Evaluation case '{options.CaseId}' was not found.");
        dataset = dataset with { Cases = [selected] };
    }
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    var baseline = JsonSerializer.Deserialize<EvaluationBaseline>(
        await File.ReadAllTextAsync(options.BaselinePath), jsonOptions)
        ?? throw new InvalidDataException("Baseline is empty.");
    using var httpClient = options.Live
        ? new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl!, UriKind.Absolute), Timeout = TimeSpan.FromMinutes(20) }
        : null;
    IEvaluationCaseExecutor executor = options.Live
        ? new ApiEvaluationCaseExecutor(httpClient!)
        : new ScriptedEvaluationCaseExecutor();
    var runner = new EvaluationRunner(executor, new EvaluationScoring(), TimeProvider.System);
    var result = await runner.RunAsync(dataset, baseline,
        options.Live ? EvaluationExecutionMode.Live : EvaluationExecutionMode.OfflineScripted);
    var store = new FileEvaluationResultStore();
    await store.SaveAsync(result,
        Path.Combine(options.OutputDirectory, "agentforge-engineering-v1-results.json"),
        Path.Combine(options.OutputDirectory, "agentforge-engineering-v1-results.md"));
    Console.WriteLine($"{result.Outcome}: {result.Cases.Count} cases; quality {result.AverageQualityScore:F2}/100; " +
        $"{result.Failed} failed; {result.NotExecuted} not executed.");
    return result.Outcome is EvaluationOutcome.Fail or EvaluationOutcome.NotExecuted ? 1 : 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Evaluation failed safely: {exception.GetType().Name}: {exception.Message}");
    return 2;
}
