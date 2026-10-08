using LocalDelegationMcp.Llm;
using System.Text.Json;

namespace LocalDelegationMcp.Tasks;

public enum DelegationStatus
{
    /// <summary>Agent loop is actively running.</summary>
    Running,

    /// <summary>Local LLM asked a question via clarify_task; waiting for the frontier to call clarify().</summary>
    WaitingClarification,

    /// <summary>Local LLM called complete_task. The session can still be resumed by delegating with this task_id.</summary>
    Completed,

    /// <summary>Stopped by stop_task (or cancelled). Cannot be resumed.</summary>
    Stopped,

    /// <summary>Failed due to an error (LLM failure, max iterations, external tool crash).</summary>
    Failed
}

/// <summary>A single delegation: the conversation history between the local LLM and its tools, plus lifecycle state.</summary>
public sealed class TaskSession : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _cts;
    private readonly List<TaskConversationEntry> _conversation = [];
    private DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;

    public required int Id { get; init; }
    /// <summary>Current delegation's description (updated on resume).</summary>
    public string Description { get; internal set; } = "";
    /// <summary>Current delegation's completion criteria (updated on resume).</summary>
    public string CompletionCriteria { get; internal set; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get { lock (_gate) return _updatedAtUtc; } }

    public event Action<TaskSession>? Changed;

    /// <summary>Full conversation history (system prompt is prepended by the agent runner, not stored here).</summary>
    private readonly List<LlmMessage> _messages = [];
    private string? _clarificationToolCallId;

    private DelegationStatus _status = DelegationStatus.Running;
    public DelegationStatus Status
    {
        get { lock (_gate) return _status; }
        internal set { lock (_gate) _status = value; }
    }

    /// <summary>The local LLM's completion summary (set when status becomes Completed).</summary>
    public string? Result { get; internal set; }

    /// <summary>The local LLM's question (set when status becomes WaitingClarification).</summary>
    public string? ClarificationQuestion { get; internal set; }

    /// <summary>Error message (set when status becomes Failed).</summary>
    public string? Error { get; internal set; }

    /// <summary>The latest progress note from the local model.</summary>
    private string? _progress;
    private DateTimeOffset? _progressUpdatedAtUtc;
    public string? Progress { get { lock (_gate) return _progress; } }
    public DateTimeOffset? ProgressUpdatedAtUtc { get { lock (_gate) return _progressUpdatedAtUtc; } }

    /// <summary>Set when the session was resumed from a previously completed one.</summary>
    public bool WasResumed { get; private set; }

    /// <summary>Number of LLM round trips consumed so far in the current run.</summary>
    private int _iterationsUsed;
    public int IterationsUsed
    {
        get { lock (_gate) return _iterationsUsed; }
        internal set { lock (_gate) _iterationsUsed = value; }
    }

    public IReadOnlyList<LlmMessage> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    public IReadOnlyList<TaskConversationEntry> Conversation
    {
        get { lock (_gate) return _conversation.ToList(); }
    }

    /// <summary>Current cancellation token (replaced on resume so a stopped session can run again).</summary>
    public CancellationToken Token => _cts.Token;

    private TaskSession()
    {
        _cts = new CancellationTokenSource();
    }

    internal static TaskSession Create(int id, string description, string completionCriteria)
    {
        var session = new TaskSession { Id = id, Description = description, CompletionCriteria = completionCriteria };
        session._conversation.Add(new TaskConversationEntry(
            DateTimeOffset.UtcNow, "frontier", "task", FormatTaskRequest(description, completionCriteria)));
        return session;
    }

    public void AddMessage(LlmMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            _updatedAtUtc = DateTimeOffset.UtcNow;
            RecordConversationMessage(message);
        }
        NotifyChanged();
    }

    public void UpdateProgress(string progress)
    {
        lock (_gate)
        {
            _progress = progress;
            _progressUpdatedAtUtc = DateTimeOffset.UtcNow;
            _updatedAtUtc = _progressUpdatedAtUtc.Value;
        }
        NotifyChanged();
    }

    /// <summary>Appends a new delegation to an existing (completed) session and resets it for another run.</summary>
    internal void ResumeWith(string description, string completionCriteria)
    {
        lock (_gate)
        {
            _status = DelegationStatus.Running;
            Result = null;
            ClarificationQuestion = null;
            Error = null;
            _progress = null;
            _progressUpdatedAtUtc = null;
            Description = description;
            CompletionCriteria = completionCriteria;
            WasResumed = true; // consumed by the agent runner after it injects the "new work" message
            _iterationsUsed = 0;
            _updatedAtUtc = DateTimeOffset.UtcNow;
            _conversation.Add(new TaskConversationEntry(_updatedAtUtc, "frontier", "task", FormatTaskRequest(description, completionCriteria)));

            // Fresh cancellation source so a previously stopped session isn't instantly cancelled.
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
        NotifyChanged();
    }

    internal void ClearResumedFlag()
    {
        lock (_gate) WasResumed = false;
    }

    internal void SetStatus(DelegationStatus status, string? result = null, string? question = null, string? error = null)
    {
        var changed = false;
        lock (_gate)
        {
            // Completed, stopped and failed states are final until an explicit completed-session resume.
            if (IsTerminalStatus(_status) && status != _status) return;

            changed = _status != status || (result is not null && Result != result) || (question is not null && ClarificationQuestion != question) || (error is not null && Error != error);
            _status = status;
            if (result != null) Result = result;
            if (question != null) ClarificationQuestion = question;
            if (error != null) Error = error;
            if (changed) _updatedAtUtc = DateTimeOffset.UtcNow;
        }
        if (changed) NotifyChanged();
    }

    public bool IsTerminal
    {
        get { lock (_gate) return IsTerminalStatus(_status); }
    }

    private static bool IsTerminalStatus(DelegationStatus status) =>
        status is DelegationStatus.Completed or DelegationStatus.Stopped or DelegationStatus.Failed;

    /// <summary>Atomically accepts one clarification and moves the session back to Running.</summary>
    internal bool TryResumeAfterClarification(string answer)
    {
        lock (_gate)
        {
            if (_status != DelegationStatus.WaitingClarification)
                return false;

            _messages.Add(LlmMessage.ToolResult(_clarificationToolCallId ?? "clarify_answer", "clarify_task", answer));
            _clarificationToolCallId = null;
            ClarificationQuestion = null;
            _status = DelegationStatus.Running;
            _updatedAtUtc = DateTimeOffset.UtcNow;
            _conversation.Add(new TaskConversationEntry(_updatedAtUtc, "frontier", "clarification_answer", answer));
        }
        NotifyChanged();
        return true;
    }

    internal void WaitForClarification(string question, string toolCallId)
    {
        lock (_gate)
        {
            _clarificationToolCallId = toolCallId;
            ClarificationQuestion = question;
            _status = DelegationStatus.WaitingClarification;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
        NotifyChanged();
    }

    /// <summary>Cancels in-flight work. Returns false only if the session is already terminal.</summary>
    internal bool TryStop()
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
                return false;
            _cts.Cancel();
            _status = DelegationStatus.Stopped;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
        NotifyChanged();
        return true;
    }

    public TaskSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TaskSessionSnapshot(
                Id,
                Description,
                CompletionCriteria,
                StatusName(_status),
                _progress,
                _progressUpdatedAtUtc,
                Result,
                ClarificationQuestion,
                Error,
                CreatedAtUtc,
                _updatedAtUtc,
                _iterationsUsed,
                _conversation.ToList());
        }
    }

    private void RecordConversationMessage(LlmMessage message)
    {
        var timestamp = DateTimeOffset.UtcNow;
        if (message.Role == "assistant")
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
                _conversation.Add(new TaskConversationEntry(timestamp, "local", "message", message.Content));

            if (message.ToolCalls is not null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    var function = toolCall.TryGetProperty("function", out var fn) ? fn : default;
                    var name = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() : null;
                    var args = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var arguments)
                        ? arguments : default;
                    var content = FormatToolArguments(args);
                    _conversation.Add(new TaskConversationEntry(timestamp, "local", "tool_call", content, name));
                }
            }
        }
        else if (message.Role == "tool" && message.Name == "clarify_task")
        {
            _conversation.Add(new TaskConversationEntry(timestamp, "frontier", "clarification_answer", message.Content ?? ""));
        }
        else if (message.Role == "tool" && message.Name == "report_progress")
        {
            // The progress tool call itself carries the useful update; its acknowledgement is noise in the transcript.
        }
        else if (message.Role == "tool")
        {
            var kind = message.Name == "complete_task" ? "final" : "tool_result";
            _conversation.Add(new TaskConversationEntry(timestamp, "local", kind, message.Content ?? "", message.Name));
        }
    }

    private static string FormatToolArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null)
            return "{}";
        if (arguments.ValueKind == JsonValueKind.String)
        {
            var raw = arguments.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return "{}";
            try
            {
                using var document = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException) { return raw; }
        }
        return JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatTaskRequest(string description, string completionCriteria) =>
        string.IsNullOrWhiteSpace(completionCriteria)
            ? description
            : $"{description}\n\nCompletion criteria: {completionCriteria}";

    private static string StatusName(DelegationStatus status) => status.ToString().ToLowerInvariant() switch
    {
        "waitingclarification" => "waiting_clarification",
        var value => value
    };

    private void NotifyChanged() => Changed?.Invoke(this);

    public void Dispose() => _cts.Dispose();
}

public sealed record TaskConversationEntry(
    DateTimeOffset TimestampUtc,
    string Speaker,
    string Kind,
    string Content,
    string? ToolName = null);

public sealed record TaskSessionSnapshot(
    int TaskId,
    string Description,
    string CompletionCriteria,
    string Status,
    string? Progress,
    DateTimeOffset? ProgressUpdatedAtUtc,
    string? Result,
    string? ClarificationQuestion,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int IterationsUsed,
    IReadOnlyList<TaskConversationEntry> Conversation);
