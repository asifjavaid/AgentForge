namespace AgentForge.Application.Llm;

public interface IStructuredOutputClient
{
    Task<T> GenerateAsync<T>(
        StructuredOutputRequest request,
        CancellationToken cancellationToken = default);
}
