using System.Text.Json;
using LocalDelegationMcp.Config;
using LocalDelegationMcp.Llm;
using LocalDelegationMcp.Mcp;
using LocalDelegationMcp.Tasks;

namespace LocalDelegationMcp.Agent;

/// <summary>Outcome of one agent run (a single delegation or a clarify-resume).</summary>
public sealed record AgentRunResult(DelegationStatus Status, string? Result = null, string? Question = null, string? Error = null);

/// <summary>
/// Drives the local LLM for a task session: sends history + tools, executes tool calls
/// (complete_task / clarify_task / external proxies), and loops until completion, clarification,
/// failure, or cancellation.
/// </summary>
public sealed class AgentRunner
{
    private readonly IChatLlm _llm;
    private readonly IExternalTools _external;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(IChatLlm llm, IExternalTools external, AgentOptions options, ILogger<AgentRunner> logger)
    {
        _llm = llm;
        _external = external;
        _options = options;
        _logger = logger;
    }

    /// <summary>Native tools every local-LLM session gets.</summary>
    private static readonly LlmToolDefinition CompleteTaskTool = new(
        "complete_task",
        "Signals that the delegated task is finished. Call only when the completion criteria are satisfied. Provide the final answer and a concise summary of the work; include important findings, decisions, and limitations.",
        Schema.Object(new() { ["task_completion_description"] = ("string", "The final answer and concise summary of completed work. State the outcome clearly and include evidence or limitations the parent needs to know.") }, required: ["task_completion_description"]));

    private static readonly LlmToolDefinition ClarifyTaskTool = new(
        "clarify_task",
        "Pauses the task and asks the parent model for information. Use only when a missing decision or input prevents correct progress; ask a specific, answerable question.",
        Schema.Object(new() { ["question"] = ("string", "A specific question the parent can answer. Include relevant choices or context when that makes the decision clearer.") }, required: ["question"]));

    private static readonly LlmToolDefinition ReportProgressTool = new(
        "report_progress",
        "Records the latest concise progress update for the parent model. Use at meaningful milestones on longer tasks; do not call repeatedly for small steps.",
        Schema.Object(new() { ["progress_description"] = ("string", "A short status note describing what has been completed and what remains or is happening now.") }, required: ["progress_description"]));

    /// <summary>Runs (or resumes) the agent loop for a session until it reaches a non-running state.</summary>
    public async Task<AgentRunResult> RunAsync(TaskSession session, CancellationToken externalStopToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token, externalStopToken);
        var ct = linkedCts.Token;

        // Build the local tool set: native tools + external servers scoped local/global.
        var tools = new List<LlmToolDefinition> { CompleteTaskTool, ClarifyTaskTool, ReportProgressTool };
        foreach (var ext in _external.ToolsForScope(McpScope.Local))
            tools.Add(new LlmToolDefinition(ext.PrefixedName, $"[{ext.ServerName}] " + ext.Description, ext.InputSchema));

        // If resuming a completed session, tell the model what's new.
        if (session.WasResumed)
        {
            session.AddMessage(LlmMessage.User(
                $"The parent has delegated additional work to this task.\n" +
                $"\nNew task: {session.Description}\nCompletion criteria: {session.CompletionCriteria}"));
            session.ClearResumedFlag(); // consumed; a later clarify-resume must not re-inject it
        }

        try
        {
            return await RunLoopAsync(session, tools, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped mid-flight (e.g. during an external tool call).
            return Finish(session, DelegationStatus.Stopped);
        }
    }

    private async Task<AgentRunResult> RunLoopAsync(TaskSession session, List<LlmToolDefinition> tools, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (session.IterationsUsed >= _options.MaxIterationsPerTask)
            {
                var msg = $"Task aborted: exceeded maximum of {_options.MaxIterationsPerTask} LLM iterations without completing.";
                _logger.LogWarning("Task {Id}: {Msg}", session.Id, msg);
                return Finish(session, DelegationStatus.Failed, error: msg);
            }

            // Compose the request: system prompt + stored history.
            var messages = new List<LlmMessage> { LlmMessage.System(BuildSystemPrompt(session)) };
            messages.AddRange(session.Messages);

            LlmResponse response;
            try
            {
                response = await _llm.ChatAsync(messages, tools, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Finish(session, DelegationStatus.Stopped);
            }
            catch (LlmException ex)
            {
                _logger.LogError(ex, "Task {Id}: LLM request failed.", session.Id);
                return Finish(session, DelegationStatus.Failed, error: $"LLM error: {ex.Message}");
            }

            session.IterationsUsed++;

            // Record the assistant turn.
            if (response.ToolCalls.Count > 0)
                session.AddMessage(LlmMessage.Assistant(response.Content, response.ToolCalls));
            else
                session.AddMessage(LlmMessage.Assistant(response.Content));

            // No tool calls: treat plain text as an implicit completion.
            if (response.ToolCalls.Count == 0)
            {
                var text = (response.Content ?? "").Trim();
                _logger.LogInformation("Task {Id}: local LLM finished without a tool call; treating reply as completion.", session.Id);
                return Finish(session, DelegationStatus.Completed, result: string.IsNullOrEmpty(text) ? "(no output)" : text);
            }

            // Execute each tool call sequentially.
            foreach (var rawCall in response.ToolCalls)
            {
                if (ct.IsCancellationRequested)
                    return Finish(session, DelegationStatus.Stopped);

                var (toolName, argsJson) = ParseToolCall(rawCall);
                _logger.LogInformation("Task {Id}: local LLM called tool '{Tool}'.", session.Id, toolName);

                string resultText;
                switch (toolName)
                {
                    case "complete_task":
                        var desc = ExtractStringArg(argsJson, "task_completion_description") ?? "(no completion description provided)";
                        session.AddMessage(LlmMessage.ToolResult(GetCallId(rawCall), toolName, desc));
                        return Finish(session, DelegationStatus.Completed, result: desc);

                    case "clarify_task":
                        var question = ExtractStringArg(argsJson, "question") ?? "The local model needs clarification but did not specify a question.";
                        session.WaitForClarification(question, GetCallId(rawCall));
                        return new AgentRunResult(DelegationStatus.WaitingClarification, Question: question);

                    case "report_progress":
                        var progress = ExtractStringArg(argsJson, "progress_description")?.Trim();
                        if (string.IsNullOrWhiteSpace(progress))
                            resultText = "ERROR: progress_description must be a non-empty string.";
                        else
                        {
                            session.UpdateProgress(progress);
                            resultText = "Progress update recorded.";
                        }
                        break;

                    default:
                        resultText = await ExecuteExternalToolAsync(toolName, argsJson, ct);
                        break;
                }

                session.AddMessage(LlmMessage.ToolResult(GetCallId(rawCall), toolName, Truncate(resultText)));
            }
        }

        return Finish(session, DelegationStatus.Stopped);
    }

    /// <summary>Persists the terminal state on the session (so task_status/pending_tasks reflect it) and returns the result.</summary>
    private static AgentRunResult Finish(TaskSession session, DelegationStatus status, string? result = null, string? question = null, string? error = null)
    {
        session.SetStatus(status, result: result, question: question, error: error);
        return new AgentRunResult(status, result, question, error);
    }

    /// <summary>One-shot pass (no task session): asks the local LLM to describe its capabilities using its full tool set.</summary>
    public async Task<string> DescribeCapabilitiesAsync(CancellationToken ct)
    {
        var tools = new List<LlmToolDefinition> { CompleteTaskTool, ClarifyTaskTool, ReportProgressTool };
        foreach (var ext in _external.ToolsForScope(McpScope.Local))
            tools.Add(new LlmToolDefinition(ext.PrefixedName, $"[{ext.ServerName}] " + ext.Description, ext.InputSchema));

        var messages = new List<LlmMessage>
        {
            LlmMessage.System("You are a local assistant. Describe your capabilities to the parent model that will delegate tasks to you: what kinds of work you can do, which tools you have available (list them by name with one-line purposes), and any limitations. Be concise but complete."),
            LlmMessage.User("Describe your capabilities now.")
        };

        var response = await _llm.ChatAsync(messages, tools, ct);
        return (response.Content ?? "").Trim();
    }

    private string BuildSystemPrompt(TaskSession session)
    {
        var sb = new System.Text.StringBuilder(_options.SystemPrompt);
        sb.Append($"\n\nYou are working on task #{session.Id}.\n");
        sb.Append($"Task: {session.Description}\n");
        sb.Append($"Completion criteria: {session.CompletionCriteria}\n");
        return sb.ToString();
    }

    private async Task<string> ExecuteExternalToolAsync(string toolName, JsonElement? argsJson, CancellationToken ct)
    {
        var ext = _external.ToolsForScope(McpScope.Local).FirstOrDefault(t => t.PrefixedName == toolName);
        if (ext is null)
            return $"ERROR: unknown tool '{toolName}'. Available tools: complete_task, clarify_task, report_progress" +
                   string.Join(", ", _external.ToolsForScope(McpScope.Local).Select(t => t.PrefixedName));

        try
        {
            Dictionary<string, JsonElement>? args = null;
            if (argsJson is { ValueKind: JsonValueKind.Object } obj)
                args = obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

            var result = await _external.InvokeAsync(ext, args, ct);
            return ExternalMcpManager.FlattenContent(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"ERROR: tool '{toolName}' failed: {ex.Message}";
        }
    }

    private static (string Name, JsonElement? Args) ParseToolCall(JsonElement rawCall)
    {
        string name = "unknown";
        JsonElement? args = null;
        if (rawCall.TryGetProperty("function", out var fn))
        {
            if (fn.TryGetProperty("name", out var n)) name = n.GetString() ?? "unknown";

            // OpenAI-compatible endpoints return arguments as a JSON *string*; some return an object. Handle both.
            if (fn.TryGetProperty("arguments", out var a))
            {
                switch (a.ValueKind)
                {
                    case JsonValueKind.Object:
                        args = a;
                        break;
                    case JsonValueKind.String when !string.IsNullOrWhiteSpace(a.GetString()):
                        try { args = JsonSerializer.Deserialize<JsonElement>(a.GetString()!); }
                        catch (JsonException) { /* malformed arguments — treat as empty */ }
                        break;
                }
            }
        }
        return (name, args);
    }

    private static string GetCallId(JsonElement rawCall) =>
        rawCall.TryGetProperty("id", out var id) ? id.GetString() ?? "call_" + Guid.NewGuid().ToString("N")[..8] : "call_" + Guid.NewGuid().ToString("N")[..8];

    private static string? ExtractStringArg(JsonElement? argsJson, string key)
    {
        if (argsJson is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var prop in obj.EnumerateObject())
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
        return null;
    }

    private static string Truncate(string s) => s.Length <= 8000 ? s : s[..8000] + "\n...[truncated]";
}

/// <summary>Small JSON-schema builder helpers for tool definitions.</summary>
internal static class Schema
{
    /// <param name="properties">property name → (json type, description)</param>
    public static JsonElement Object(Dictionary<string, (string Type, string Description)> properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>
            {
                ["type"] = kv.Value.Type,
                ["description"] = kv.Value.Description
            })
        };

        if (required is { Length: > 0 }) schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema);
    }
}
