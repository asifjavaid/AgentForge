namespace AgentForge.Api.Contracts;

public sealed record AskKnowledgeRequest(string? Question, string? Source = null);
