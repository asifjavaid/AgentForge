using AgentForge.Application.Agents;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.ToolCalling;

internal static class ChatToolCallingProtocol
{
    internal static async Task<ToolCallingResponse> CompleteAsync(
        ChatClient client,
        string provider,
        string model,
        ToolCallingRequest request,
        CancellationToken cancellationToken)
    {
        var messages = request.Messages.Select(ToChatMessage).ToArray();
        var options = new ChatCompletionOptions
        {
            AllowParallelToolCalls = false,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                request.ResponseSchemaName,
                BinaryData.FromString(request.ResponseJsonSchema),
                jsonSchemaIsStrict: true)
        };

        foreach (var tool in request.Tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.JsonSchema),
                functionSchemaIsStrict: true));
        }

        ChatCompletion completion = await client.CompleteChatAsync(messages, options, cancellationToken);

        if (completion.FinishReason is ChatFinishReason.Length or ChatFinishReason.ContentFilter ||
            !string.IsNullOrWhiteSpace(completion.Refusal))
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The model did not return a complete repository-agent response.");
        }

        var calls = completion.ToolCalls
            .Select(call => new AgentToolCall(
                call.Id,
                call.FunctionName,
                call.FunctionArguments.ToString()))
            .ToArray();
        var finalJson = completion.Content.Count > 0
            ? completion.Content[0].Text
            : null;

        return new ToolCallingResponse(
            provider,
            model,
            finalJson,
            calls,
            new AgentTokenUsage(
                completion.Usage?.InputTokenCount,
                completion.Usage?.OutputTokenCount,
                completion.Usage?.TotalTokenCount));
    }

    private static ChatMessage ToChatMessage(AgentMessage message) => message.Role switch
    {
        AgentMessageRole.System => new SystemChatMessage(message.Content ?? string.Empty),
        AgentMessageRole.User => new UserChatMessage(message.Content ?? string.Empty),
        AgentMessageRole.Assistant => new AssistantChatMessage(
            (message.ToolCalls ?? [])
                .Select(call => ChatToolCall.CreateFunctionToolCall(
                    call.Id,
                    call.Name,
                    BinaryData.FromString(call.ArgumentsJson)))),
        AgentMessageRole.Tool when !string.IsNullOrWhiteSpace(message.ToolCallId) =>
            new ToolChatMessage(message.ToolCallId, message.Content ?? string.Empty),
        _ => throw new AgentOperationException(
            AgentFailureKind.InvalidResponse,
            "The agent conversation contained an invalid message.")
    };
}
