using System.Text.Json;
using System.ComponentModel;
using LocalDelegationMcp.Config;
using LocalDelegationMcp.Llm;
using LocalDelegationMcp.Agent;
using LocalDelegationMcp.Mcp;
using LocalDelegationMcp.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalDelegationMcp.Tools;

/// <summary>Builds the seven frontier-facing MCP tools that coordinate delegation to the local LLM.</summary>
public static class FrontierTools
{
    public static IReadOnlyList<McpServerTool> BuildAll(
        TaskRegistry registry,
        AgentRunner runner,
        IExternalTools external,
        IServiceProvider services)
    {
        return new List<McpServerTool>
        {
            CreateDelegateTask(registry, runner, services),
            CreateDelegateTaskAsync(registry, runner, services),
            CreatePendingTasks(registry, services),
            CreateStopTask(registry, services),
            CreateDelegationStatus(registry, services),
            CreateClarify(registry, runner, services),
            CreateDescribeCapabilities(runner, external, services)
        };
    }

    private static McpServerTool CreateDelegateTask(TaskRegistry registry, AgentRunner runner, IServiceProvider services) =>
        McpServerTool.Create(async (
            [Description("The concrete work request for the local model. Include relevant context, constraints, and desired output format.")] string task_description,
            [Description("Observable conditions the result must satisfy. Leave empty only when the request itself makes success clear.")] string completion_criteria = "",
            [Description("Optional id of an existing completed task to continue with its saved conversation. Omit to start a new task; running, waiting, stopped, and failed tasks cannot be resumed.")] int task_id = 0) =>
        {
            TaskSession session;
            try
            {
                (session, _) = registry.StartDelegation(task_description, completion_criteria, task_id == 0 ? null : task_id);
            }
            catch (InvalidOperationException ex)
            {
                return ErrorResult(ex.Message);
            }

            var result = await runner.RunAsync(session);
            return DelegationResponse(session, result);
        }, new()
        {
            Name = "delegate_task",
            Description = "Run a task with the local model and wait for the outcome. Use when you need the result in this turn. Returns completed with a result, waiting_clarification with a question to answer using clarify, stopped, or failed. For longer work where you can continue without waiting, use delegate_task_async. Only a completed task can be continued by passing its task_id.",
            Services = services
        });

    private static McpServerTool CreateDelegateTaskAsync(TaskRegistry registry, AgentRunner runner, IServiceProvider services) =>
        McpServerTool.Create((
            [Description("The concrete work request for the local model. Include relevant context, constraints, and desired output format.")] string task_description,
            [Description("Observable conditions the result must satisfy. Leave empty only when the request itself makes success clear.")] string completion_criteria = "",
            [Description("Optional id of an existing completed task to continue with its saved conversation. Omit to start a new task; running, waiting, stopped, and failed tasks cannot be resumed.")] int task_id = 0) =>
        {
            TaskSession session;
            try
            {
                (session, _) = registry.StartDelegation(task_description, completion_criteria, task_id == 0 ? null : task_id);
            }
            catch (InvalidOperationException ex)
            {
                return ErrorResult(ex.Message);
            }

            // Fire and forget: the agent loop runs in the background; poll with task_status.
            _ = Task.Run(async () =>
            {
                try { await runner.RunAsync(session); }
                catch (Exception ex)
                {
                    session.SetStatus(DelegationStatus.Failed, error: $"Agent loop crashed: {ex.Message}");
                }
            });

            return OkResult(new
            {
                task_id = session.Id,
                status = "running",
                message = "Task started in the background. Use task_status(task_id) to check progress and retrieve the result."
            });
        }, new()
        {
            Name = "delegate_task_async",
            Description = "Start a task with the local model in the background and return immediately. Use for longer work or when the parent can do other work while it runs. Save the returned task_id, then inspect progress and outcomes with task_status or pending_tasks. Only a completed task can be continued by passing its task_id. New tasks are rejected when the configured active-task limit is reached.",
            Services = services
        });

    private static McpServerTool CreatePendingTasks(TaskRegistry registry, IServiceProvider services) =>
        McpServerTool.Create(() =>
        {
            var pending = registry.Pending().Select(s => new
            {
                task_id = s.Id,
                task_description = s.Description,
                task_status = StatusName(s.Status),
                clarification_question = s.ClarificationQuestion,
                progress = s.Progress,
                progress_updated_at_utc = s.ProgressUpdatedAtUtc?.ToString("O"),
                iterations_used = s.IterationsUsed,
                created_at_utc = s.CreatedAtUtc.ToString("O")
            }).ToList();

            return OkResult(new { pending_tasks = pending });
        }, new()
        {
            Name = "pending_tasks",
            Description = "Lists running tasks and tasks waiting for clarification. Each entry includes task_id, description, status, the latest progress note when available, and any clarification question. An empty list means no task is active.",
            Services = services
        });

    private static McpServerTool CreateStopTask(TaskRegistry registry, IServiceProvider services) =>
        McpServerTool.Create(([Description("Id returned by delegate_task or delegate_task_async.")] int task_id) =>
        {
            if (!registry.TryGet(task_id, out var session) || session is null)
                return ErrorResult($"Task {task_id} does not exist.");

            if (session.IsTerminal)
                return OkResult(new { task_id = session.Id, status = StatusName(session.Status), message = $"Task was already {StatusName(session.Status)}; nothing to stop." });

            var stopped = session.TryStop();
            return stopped
                ? OkResult(new { task_id = session.Id, status = "stopped", message = "Task stopped successfully. Its context is no longer active and cannot be resumed." })
                : ErrorResult($"Failed to stop task {task_id}.");
        }, new()
        {
            Name = "stop_task",
            Description = "Stops a pending (running or waiting) delegated task. Use when the task's context is no longer needed. Stopped tasks cannot be resumed.",
            Services = services
        });

    private static McpServerTool CreateDelegationStatus(TaskRegistry registry, IServiceProvider services) =>
        McpServerTool.Create(([Description("Id returned when the task was created.")] int task_id) =>
        {
            if (!registry.TryGet(task_id, out var session) || session is null)
                return ErrorResult($"Task {task_id} does not exist.");

            var payload = new Dictionary<string, object?>
            {
                ["task_id"] = session.Id,
                ["status"] = StatusName(session.Status),
                ["progress"] = session.Progress,
                ["progress_updated_at_utc"] = session.ProgressUpdatedAtUtc?.ToString("O"),
                ["iterations_used"] = session.IterationsUsed
            };

            switch (session.Status)
            {
                case DelegationStatus.Completed:
                    payload["result"] = session.Result;
                    break;
                case DelegationStatus.WaitingClarification:
                    payload["clarification_question"] = session.ClarificationQuestion;
                    payload["message"] = "The local LLM is waiting for your answer. Use clarify(task_id, clarification_description) to respond.";
                    break;
                case DelegationStatus.Failed:
                    payload["error"] = session.Error;
                    break;
                case DelegationStatus.Stopped:
                    payload["message"] = "Task was stopped and cannot be resumed.";
                    break;
            }

            return OkResult(payload);
        }, new()
        {
            Name = "task_status",
            Description = "Checks one delegated task. For a running task, returns its latest report_progress note when available. For a waiting task, returns the question to answer with clarify. Completed tasks include the result; failed tasks include the error. Use the task_id returned at creation.",
            Services = services
        });

    private static McpServerTool CreateClarify(TaskRegistry registry, AgentRunner runner, IServiceProvider services) =>
        McpServerTool.Create((
            [Description("Id of a task whose status is waiting_clarification.")] int task_id,
            [Description("Your answer to the local model's clarification question. Include any constraints or context needed to continue.")] string clarification_description) =>
        {
            if (!registry.TryGet(task_id, out var session) || session is null)
                return ErrorResult($"Task {task_id} does not exist.");

            if (!session.TryResumeAfterClarification(clarification_description))
                return ErrorResult($"Task {task_id} is '{StatusName(session.Status)}', not waiting for clarification. Only tasks in 'waiting_clarification' status can be clarified.");

            // The answer and state transition are atomic, so duplicate clarify calls cannot start two loops.
            _ = Task.Run(async () =>
            {
                try { await runner.RunAsync(session); }
                catch (Exception ex)
                {
                    session.SetStatus(DelegationStatus.Failed, error: $"Agent loop crashed after clarify: {ex.Message}");
                }
            });

            return OkResult(new
            {
                task_id = session.Id,
                status = "running",
                message = "Clarification delivered; the local LLM is resuming work. Check progress with task_status(task_id)."
            });
        }, new()
        {
            Name = "clarify",
            Description = "Answer the outstanding question for a task in waiting_clarification status. This resumes the local model in the background; the answer should resolve the specific missing information. Check the eventual outcome with task_status. Each clarification can be accepted only once.",
            Services = services
        });

    private static McpServerTool CreateDescribeCapabilities(AgentRunner runner, IExternalTools external, IServiceProvider services) =>
        McpServerTool.Create(async () =>
        {
            try
            {
                var description = await runner.DescribeCapabilitiesAsync(CancellationToken.None);
                return OkResult(new { capabilities = description });
            }
            catch (Exception ex)
            {
                // Fall back to a static summary of the tool inventory if the LLM call fails.
                var localTools = external.ToolsForScope(McpScope.Local).Select(t => $"{t.PrefixedName}: {t.Description}");
                return OkResult(new
                {
                    capabilities = $"(LLM unavailable: {ex.Message}) Static capability summary — native tools: complete_task (finish a task), clarify_task (ask the parent for missing information), report_progress (publish a concise status update). External tools available to the local LLM:\n" + string.Join("\n", localTools)
                });
            }
        }, new()
        {
            Name = "describe_capabilities",
            Description = "Ask the local model to summarize its capabilities and the local/global tools it can use. Use this before delegating when you need to judge whether the configured local model and tools can handle a task.",
            Services = services
        });

    // ---------- helpers ----------

    private static string StatusName(DelegationStatus status) => status switch
    {
        DelegationStatus.Running => "running",
        DelegationStatus.WaitingClarification => "waiting_clarification",
        DelegationStatus.Completed => "completed",
        DelegationStatus.Stopped => "stopped",
        DelegationStatus.Failed => "failed",
        _ => status.ToString().ToLowerInvariant()
    };

    private static CallToolResult DelegationResponse(TaskSession session, AgentRunResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["task_id"] = session.Id,
            ["status"] = StatusName(session.Status)
        };

        switch (session.Status)
        {
            case DelegationStatus.Completed:
                payload["result"] = result.Result;
                break;
            case DelegationStatus.WaitingClarification:
                payload["clarification_question"] = result.Question;
                payload["message"] = "The local LLM needs clarification. Answer with clarify(task_id, clarification_description).";
                break;
            case DelegationStatus.Failed:
                payload["error"] = result.Error;
                break;
            case DelegationStatus.Stopped:
                payload["message"] = "Task was stopped.";
                break;
        }

        return OkResult(payload);
    }

    private static CallToolResult OkResult(object? payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOpts) }],
        IsError = false
    };

    private static CallToolResult ErrorResult(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
