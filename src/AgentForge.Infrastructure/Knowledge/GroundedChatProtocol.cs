using System.Diagnostics;
using System.Text.Json;
using AgentForge.Application.Knowledge;
using AgentForge.Application.Llm;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.Knowledge;

internal static class GroundedChatProtocol
{
    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["answer","citations","isSufficientEvidence"],"properties":{"answer":{"type":"string"},"citations":{"type":"array","items":{"type":"string"}},"isSufficientEvidence":{"type":"boolean"}}}
        """;

    public static async Task<GroundedGenerationResult> GenerateAsync(
        ChatClient client, string provider, string model, GroundedGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var context = string.Join("\n\n", request.Context.Select(chunk =>
            $"SOURCE IDENTIFIER: {chunk.Source}#{chunk.ChunkId}\n<document>\n{chunk.Text}\n</document>"));
        var userMessage = $"USER QUESTION\n{request.Question}\n\nRETRIEVED CONTEXT\n{context}";
        var timer = Stopwatch.StartNew();
        ChatCompletion completion = await client.CompleteChatAsync(
            [new SystemChatMessage(request.SystemPrompt), new UserChatMessage(userMessage)],
            new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    "grounded_knowledge_answer", BinaryData.FromString(Schema), jsonSchemaIsStrict: true)
            }, cancellationToken);
        timer.Stop();

        if (completion.FinishReason != ChatFinishReason.Stop ||
            !string.IsNullOrWhiteSpace(completion.Refusal) || completion.Content.Count == 0)
            throw new LlmOperationException(LlmFailureKind.InvalidResponse, "The model did not return a complete grounded answer.");

        var payload = JsonSerializer.Deserialize<GroundedPayload>(completion.Content[0].Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new LlmOperationException(LlmFailureKind.InvalidResponse, "The model returned an empty grounded answer.");
        if (string.IsNullOrWhiteSpace(payload.Answer) || payload.Citations is null)
            throw new LlmOperationException(LlmFailureKind.InvalidResponse, "The model returned an invalid grounded answer.");

        return new GroundedGenerationResult(
            payload.Answer, payload.Citations, payload.IsSufficientEvidence, provider, model, timer.ElapsedMilliseconds,
            completion.Usage?.InputTokenCount, completion.Usage?.OutputTokenCount, completion.Usage?.TotalTokenCount);
    }

    private sealed record GroundedPayload(string Answer, IReadOnlyList<string> Citations, bool IsSufficientEvidence);
}
