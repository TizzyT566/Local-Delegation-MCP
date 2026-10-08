using System.Text.Json;

namespace LocalDelegationMcp.Llm;

/// <summary>A tool definition as sent to an OpenAI-compatible chat completions endpoint.</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>One message in the conversation history (OpenAI role format).</summary>
public sealed class LlmMessage
{
    public required string Role { get; set; } // system | user | assistant | tool
    public string? Content { get; set; }
    /// <summary>assistant only: name of the tool called.</summary>
    public string? ToolCallId { get; set; }
    /// <summary>tool role only: id of the tool call this result belongs to.</summary>
    public string? Name { get; set; }
    /// <summary>assistant only: raw tool calls as returned by the model (id, function.name, function.arguments).</summary>
    public List<JsonElement>? ToolCalls { get; set; }

    public static LlmMessage System(string text) => new() { Role = "system", Content = text };
    public static LlmMessage User(string text) => new() { Role = "user", Content = text };
    public static LlmMessage Assistant(string? content, List<JsonElement>? toolCalls = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls };
    public static LlmMessage ToolResult(string toolCallId, string name, string content) =>
        new() { Role = "tool", ToolCallId = toolCallId, Name = name, Content = content };
}

/// <summary>The model's reply to one chat completion request.</summary>
public sealed class LlmResponse
{
    public required string? Content { get; set; }
    /// <summary>Raw tool calls from the assistant message (each: id, type=function, function{name, arguments}).</summary>
    public List<JsonElement> ToolCalls { get; set; } = [];
}

/// <summary>Abstraction over an OpenAI-compatible chat completions endpoint so tests can substitute a fake.</summary>
public interface IChatLlm
{
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        CancellationToken cancellationToken);
}
