namespace TestAgentHarness.Model;

/// <summary>A tool the model asked the harness to run. Arguments are a JSON object as text.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One message in the conversation the harness sends to the model.</summary>
public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string? content, IReadOnlyList<ToolCall> toolCalls) =>
        new("assistant", content, toolCalls.Count > 0 ? toolCalls : null);
    public static ChatMessage ToolResult(string toolCallId, string content) =>
        new("tool", content, ToolCallId: toolCallId);
}

/// <summary>What the model is told about a tool: its name, purpose and JSON-schema parameters.</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchemaJson);

/// <summary>The model either calls tools or gives a final answer (no tool calls).</summary>
public sealed record ModelResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>
/// The only thing the harness needs from a model. Everything else in this
/// repository is ordinary code wrapped around this one call.
/// </summary>
public interface IModelClient
{
    string Name { get; }

    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default);
}
