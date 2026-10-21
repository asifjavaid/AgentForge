using AgentForge.Application.Evaluation;

namespace AgentForge.Api.Composition;

public static class EvaluationComposition
{
    public static IServiceCollection AddEvaluationAccounting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Pricing");
        var pricing = new PricingCatalog(
            section["Currency"] ?? "USD",
            section.GetSection("Models").Get<ModelPrice[]>() ?? []);
        if (string.IsNullOrWhiteSpace(pricing.Currency) || pricing.Models.Any(item =>
                string.IsNullOrWhiteSpace(item.Provider) || string.IsNullOrWhiteSpace(item.Model) ||
                item.InputPerMillionTokens < 0 || item.OutputPerMillionTokens < 0 ||
                item.CachedInputPerMillionTokens < 0 || item.ReasoningPerMillionTokens < 0))
            throw new InvalidOperationException("Pricing configuration is invalid.");
        services.AddSingleton(pricing);
        services.AddSingleton<CostAccounting>();
        return services;
    }
}
