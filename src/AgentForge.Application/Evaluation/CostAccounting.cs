namespace AgentForge.Application.Evaluation;

public sealed class CostAccounting(PricingCatalog catalog)
{
    public EvaluationCostBreakdown EstimateBreakdown(IEnumerable<ModelUsageRecord> usage)
    {
        var records = usage.ToArray();
        return new EvaluationCostBreakdown(
            Estimate(records),
            records.GroupBy(item => item.WorkflowId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Estimate(group), StringComparer.Ordinal),
            records.GroupBy(item => item.Role).ToDictionary(group => group.Key, group => Estimate(group)),
            records.GroupBy(item => $"{item.Provider}/{item.Model}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Estimate(group), StringComparer.OrdinalIgnoreCase));
    }

    public EvaluationCostEstimate Estimate(IEnumerable<ModelUsageRecord> usage)
    {
        decimal total = 0;
        foreach (var item in usage)
        {
            var price = catalog.Models.SingleOrDefault(model =>
                string.Equals(model.Provider, item.Provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(model.Model, item.Model, StringComparison.OrdinalIgnoreCase));
            if (price is null || item.InputTokens is null || item.OutputTokens is null)
                return new EvaluationCostEstimate(catalog.Currency, null, "CostEstimateUnavailable");
            var ordinaryInput = item.InputTokens.Value - (item.CachedInputTokens ?? 0);
            if (ordinaryInput < 0 || item.CachedInputTokens is not null && price.CachedInputPerMillionTokens is null ||
                item.ReasoningTokens is not null && price.ReasoningPerMillionTokens is null)
                return new EvaluationCostEstimate(catalog.Currency, null, "CostEstimateUnavailable");
            total += ordinaryInput / 1_000_000m * price.InputPerMillionTokens;
            total += item.OutputTokens.Value / 1_000_000m * price.OutputPerMillionTokens;
            total += (item.CachedInputTokens ?? 0) / 1_000_000m * (price.CachedInputPerMillionTokens ?? 0);
            total += (item.ReasoningTokens ?? 0) / 1_000_000m * (price.ReasoningPerMillionTokens ?? 0);
        }
        return new EvaluationCostEstimate(catalog.Currency, decimal.Round(total, 8), "EstimatedNotInvoice");
    }
}
