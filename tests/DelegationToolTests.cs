using LocalDelegationMcp.Llm;
using LocalDelegationMcp.Tests;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace LocalDelegationMcp.Tests;

/// <summary>End-to-end tests of the seven frontier-facing tools through a real MCP client/server pair.</summary>
public sealed class DelegationToolTests : IAsyncLifetime
{
    private TestDelegationServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new TestDelegationServer();
        await _server.ConnectClientAsync();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public void TaskRegistry_RejectsWorkAboveConfiguredActiveLimit()
    {
        using var registry = new LocalDelegationMcp.Tasks.TaskRegistry(maxActiveTasks: 1);
        registry.StartDelegation("first", "", taskId: null);

        var error = Assert.Throws<InvalidOperationException>(() => registry.StartDelegation("second", "", taskId: null));
        Assert.Contains("Maximum active task limit reached (1)", error.Message);
    }

    // ------------------------------------------------------------------
    // delegate_task (synchronous)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DelegateTask_CompletesViaCompleteTaskTool()
    {
        _server.Llm.Enqueue(FakeLlm.Complete("I did the thing. Result: 42."));

        var r = await _server.Call("delegate_task", new()
        {
            ["task_description"] = "Compute the answer to life.",
            ["completion_criteria"] = "A number is returned."
        });

        Assert.False(r.IsError);
        var json = TestDelegationServer.Parse(r);
        Assert.Equal(1, json.GetProperty("task_id").GetInt32());
        Assert.Equal("completed", json.GetProperty("status").GetString());
        Assert.Contains("42", json.GetProperty("result").GetString());

        // The LLM must have received the system prompt + user task and both native tools.
        var request = _server.Llm.Requests.Single();
        Assert.Contains(request.Messages, m => m.Role == "system" && m.Content!.Contains("Compute the answer to life."));
        var toolNames = request.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("complete_task", toolNames);
        Assert.Contains("clarify_task", toolNames);
        Assert.Contains("report_progress", toolNames);
    }

    [Fact]
    public async Task ReportProgress_IsVisibleInTaskStatus()
    {
        _server.Llm.Enqueue(FakeLlm.ToolCall("report_progress", new { progress_description = "Reviewed the inputs; comparing the two options now." }));
        _server.Llm.Enqueue(FakeLlm.Complete("Option A is the better fit."));

        var delegated = await _server.Call("delegate_task", new() { ["task_description"] = "Compare the options." });
        var taskId = TestDelegationServer.Parse(delegated).GetProperty("task_id").GetInt32();
        var status = TestDelegationServer.Parse(await _server.Call("task_status", new() { ["task_id"] = taskId }));

        Assert.Equal("completed", status.GetProperty("status").GetString());
        Assert.Equal("Reviewed the inputs; comparing the two options now.", status.GetProperty("progress").GetString());
        Assert.Equal("Option A is the better fit.", status.GetProperty("result").GetString());
    }

    [Fact]
    public async Task FrontierToolSchemas_IncludeParameterDescriptions()
    {
        var tools = await _server.Client.ListToolsAsync();
        var delegateTool = tools.Single(t => t.Name == "delegate_task");
        var schemaJson = JsonSerializer.Serialize(delegateTool.ProtocolTool.InputSchema);

        Assert.Contains("concrete work request", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("observable conditions", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("completed task", schemaJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DelegateTask_PlainTextReplyIsImplicitCompletion()
    {
        _server.Llm.Enqueue(FakeLlm.Text("Here is my answer: the sky."));

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "What color is the sky?" });

        Assert.False(r.IsError);
        var json = TestDelegationServer.Parse(r);
        Assert.Equal("completed", json.GetProperty("status").GetString());
        Assert.Contains("the sky", json.GetProperty("result").GetString());
    }

    [Fact]
    public async Task DelegateTask_ExternalToolCallIsExecutedAndResultFedBack()
    {
        // 1st call: local LLM uses a local-scoped external tool; 2nd call: completes.
        _server.Llm.Enqueue(FakeLlm.ToolCall("local_read_file", new { path = "/tmp/x" }));
        _server.Llm.Enqueue(FakeLlm.Complete("File read successfully."));

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "Read /tmp/x" });

        Assert.False(r.IsError);
        Assert.Equal("completed", TestDelegationServer.Parse(r).GetProperty("status").GetString());

        // The external tool was invoked with the forwarded arguments.
        var invocation = _server.External.Invocations.Single();
        Assert.Equal("local_read_file", invocation.PrefixedName);
        Assert.Equal("/tmp/x", invocation.Args!["path"].GetString());

        // The second LLM request must contain the assistant tool call + tool result in history.
        var secondRequest = _server.Llm.Requests[1];
        Assert.Contains(secondRequest.Messages, m => m.Role == "assistant" && m.ToolCalls is { Count: 1 });
        Assert.Contains(secondRequest.Messages, m => m.Role == "tool" && m.Content!.Contains("ok:local_read_file"));
    }

    [Fact]
    public async Task DelegateTask_UnknownExternalToolReturnsErrorToLlm()
    {
        _server.Llm.Enqueue(FakeLlm.ToolCall("nope_missing_tool", new { }));
        _server.Llm.Enqueue(FakeLlm.Complete("Recovered."));

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "Try a missing tool" });

        Assert.Equal("completed", TestDelegationServer.Parse(r).GetProperty("status").GetString());
        var secondRequest = _server.Llm.Requests[1];
        Assert.Contains(secondRequest.Messages, m => m.Role == "tool" && m.Content!.StartsWith("ERROR: unknown tool"));
    }

    [Fact]
    public async Task DelegateTask_MaxIterationsFailsTheTask()
    {
        // The LLM keeps calling an external tool forever.
        for (var i = 0; i < 20; i++)
            _server.Llm.Enqueue(FakeLlm.ToolCall("local_read_file", new { path = "/tmp/x" }));

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "Loop forever" });

        Assert.False(r.IsError); // tool-level success; the *task* failed
        var json = TestDelegationServer.Parse(r);
        Assert.Equal("failed", json.GetProperty("status").GetString());
        Assert.Contains("maximum", json.GetProperty("error").GetString()!.ToLowerInvariant());
    }

    [Fact]
    public async Task DelegateTask_LlmErrorFailsTheTask()
    {
        // Verify failure handling directly through the agent runner with a throwing LLM.
        var registry = new LocalDelegationMcp.Tasks.TaskRegistry();
        var session = registry.Create("t", "c");
        var throwingLlm = new ThrowingLlm();
        var runner = new LocalDelegationMcp.Agent.AgentRunner(throwingLlm, _server.External, new LocalDelegationMcp.Config.AgentOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalDelegationMcp.Agent.AgentRunner>.Instance);
        var result = await runner.RunAsync(session);

        Assert.Equal(LocalDelegationMcp.Tasks.DelegationStatus.Failed, result.Status);
        Assert.Contains("LLM error", result.Error!);
    }

    // ------------------------------------------------------------------
    // clarify round trip
    // ------------------------------------------------------------------

    [Fact]
    public async Task Clarify_RoundTrip_ResumesAndCompletes()
    {
        // 1st: local LLM asks a question.
        _server.Llm.Enqueue(FakeLlm.Clarify("Which file should I read?"));

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "Read the right file" });
        Assert.False(r.IsError);
        var json = TestDelegationServer.Parse(r);
        var taskId = json.GetProperty("task_id").GetInt32();
        Assert.Equal("waiting_clarification", json.GetProperty("status").GetString());
        Assert.Contains("Which file", json.GetProperty("clarification_question").GetString()!);

        // 2nd: frontier answers via clarify().
        var c = await _server.Call("clarify", new() { ["task_id"] = taskId, ["clarification_description"] = "Read /etc/hosts" });
        Assert.False(c.IsError);
        var cj = TestDelegationServer.Parse(c);
        Assert.Equal(taskId, cj.GetProperty("task_id").GetInt32());
        Assert.Equal("running", cj.GetProperty("status").GetString());

        // 3rd: local LLM completes after receiving the answer.
        _server.Llm.Enqueue(FakeLlm.Complete("Read /etc/hosts as instructed."));

        var final = await _server.WaitForTerminalAsync(taskId);
        Assert.Equal("completed", TestDelegationServer.Parse(final).GetProperty("status").GetString());

        // The resumed LLM request must include the clarify answer as a tool result.
        var resumeRequest = _server.Llm.Requests.Last();
        Assert.Contains(resumeRequest.Messages, m => m.Role == "tool" && m.Content!.Contains("/etc/hosts"));
    }

    [Fact]
    public async Task Clarify_OnNonWaitingTask_ReturnsError()
    {
        // No task exists yet.
        var r = await _server.Call("clarify", new() { ["task_id"] = 99, ["clarification_description"] = "hello" });
        Assert.True(r.IsError);

        // Create a running (never-completed) task via async delegation that stalls in a blocking external tool.
        _server.External.BlockUntilCancelled = true;
        _server.Llm.Enqueue(FakeLlm.ToolCall("local_read_file", new { path = "/x" }));
        var d = await _server.Call("delegate_task_async", new() { ["task_description"] = "stall" });
        var taskId = TestDelegationServer.Parse(d).GetProperty("task_id").GetInt32();

        // Wait until the background loop is actually running, then clarify must be rejected.
        await _server.WaitForStatusAsync(taskId, "running");
        var r2 = await _server.Call("clarify", new() { ["task_id"] = taskId, ["clarification_description"] = "hello" });
        Assert.True(r2.IsError);

        // Cleanup: stop the stalled task.
        await _server.Call("stop_task", new() { ["task_id"] = taskId });
    }

    [Fact]
    public async Task Clarify_OnUnknownTask_ReturnsError()
    {
        var r = await _server.Call("clarify", new() { ["task_id"] = 12345, ["clarification_description"] = "hi" });
        Assert.True(r.IsError);
        Assert.Contains("does not exist", TextOf(r));
    }

    // ------------------------------------------------------------------
    // delegate_task_async + task_status + pending_tasks
    // ------------------------------------------------------------------

    [Fact]
    public async Task DelegateTaskAsync_ReturnsImmediatelyAndCompletesInBackground()
    {
        _server.Llm.Enqueue(FakeLlm.Complete("Async done."));

        var r = await _server.Call("delegate_task_async", new() { ["task_description"] = "Do it async" });
        Assert.False(r.IsError);
        var json = TestDelegationServer.Parse(r);
        var taskId = json.GetProperty("task_id").GetInt32();
        Assert.Equal("running", json.GetProperty("status").GetString());

        // It should not be in pending_tasks once finished.
        var final = await _server.WaitForTerminalAsync(taskId);
        Assert.Equal("completed", TestDelegationServer.Parse(final).GetProperty("status").GetString());
        Assert.Contains("Async done.", TestDelegationServer.Parse(final).GetProperty("result").GetString()!);

        var pending = await _server.Call("pending_tasks");
        var pjson = TestDelegationServer.Parse(pending);
        Assert.Equal(0, pjson.GetProperty("pending_tasks").GetArrayLength());
    }

    [Fact]
    public async Task PendingTasks_ListsRunningAndWaitingTasks()
    {
        // One task that will wait for clarification (stays pending).
        _server.Llm.Enqueue(FakeLlm.Clarify("Need info."));
        var d = await _server.Call("delegate_task_async", new() { ["task_description"] = "Will clarify" });
        var taskId = TestDelegationServer.Parse(d).GetProperty("task_id").GetInt32();

        // Wait until it reaches waiting_clarification (bounded — fails instead of hanging if it never does).
        await _server.WaitForStatusAsync(taskId, "waiting_clarification");

        var pending = TestDelegationServer.Parse(await _server.Call("pending_tasks"));
        var tasks = pending.GetProperty("pending_tasks");
        Assert.Equal(1, tasks.GetArrayLength());
        var entry = tasks[0];
        Assert.Equal(taskId, entry.GetProperty("task_id").GetInt32());
        Assert.Equal("waiting_clarification", entry.GetProperty("task_status").GetString());
        Assert.Contains("Need info.", entry.GetProperty("clarification_question").GetString()!);

        // Cleanup.
        await _server.Call("stop_task", new() { ["task_id"] = taskId });
    }

    [Fact]
    public async Task TaskStatus_UnknownTask_ReturnsError()
    {
        var r = await _server.Call("task_status", new() { ["task_id"] = 424242 });
        Assert.True(r.IsError);
        Assert.Contains("does not exist", TextOf(r));
    }

    [Fact]
    public async Task TaskStatus_CompletedTask_IncludesResult()
    {
        _server.Llm.Enqueue(FakeLlm.Complete("Final answer: 7."));
        var r = await _server.Call("delegate_task", new() { ["task_description"] = "Answer" });
        var taskId = TestDelegationServer.Parse(r).GetProperty("task_id").GetInt32();

        var s = TestDelegationServer.Parse(await _server.Call("task_status", new() { ["task_id"] = taskId }));
        Assert.Equal("completed", s.GetProperty("status").GetString());
        Assert.Contains("7.", s.GetProperty("result").GetString()!);
    }

    // ------------------------------------------------------------------
    // stop_task
    // ------------------------------------------------------------------

    [Fact]
    public async Task StopTask_StopsRunningTask_AndCannotBeResumed()
    {
        // Stall the task in a blocking external tool.
        _server.External.BlockUntilCancelled = true;
        _server.Llm.Enqueue(FakeLlm.ToolCall("local_read_file", new { path = "/x" }));
        var d = await _server.Call("delegate_task_async", new() { ["task_description"] = "stall for stopping" });
        var taskId = TestDelegationServer.Parse(d).GetProperty("task_id").GetInt32();

        // Wait until it is actually running.
        await _server.WaitForStatusAsync(taskId, "running");

        var s = await _server.Call("stop_task", new() { ["task_id"] = taskId });
        Assert.False(s.IsError);
        var sj = TestDelegationServer.Parse(s);
        Assert.Equal("stopped", sj.GetProperty("status").GetString());

        // Stopped tasks cannot be resumed by delegating with their id.
        var r = await _server.Call("delegate_task", new() { ["task_description"] = "resume stopped", ["task_id"] = taskId });
        Assert.True(r.IsError);

        // Stopping again reports already terminal.
        var s2 = TestDelegationServer.Parse(await _server.Call("stop_task", new() { ["task_id"] = taskId }));
        Assert.Equal("stopped", s2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StopTask_UnknownTask_ReturnsError()
    {
        var r = await _server.Call("stop_task", new() { ["task_id"] = 777 });
        Assert.True(r.IsError);
        Assert.Contains("does not exist", TextOf(r));
    }

    [Fact]
    public async Task StopTask_CompletedTaskRemainsCompleted()
    {
        _server.Llm.Enqueue(FakeLlm.Complete("Done."));
        var delegated = await _server.Call("delegate_task", new() { ["task_description"] = "Finish" });
        var taskId = TestDelegationServer.Parse(delegated).GetProperty("task_id").GetInt32();

        var stopped = TestDelegationServer.Parse(await _server.Call("stop_task", new() { ["task_id"] = taskId }));
        Assert.Equal("completed", stopped.GetProperty("status").GetString());
        Assert.Equal("completed", TestDelegationServer.Parse(await _server.Call("task_status", new() { ["task_id"] = taskId })).GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------
    // resume (continue a completed task with its context)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DelegateTask_WithCompletedTaskId_ReusesContext()
    {
        // First delegation completes.
        _server.Llm.Enqueue(FakeLlm.Complete("First part done."));
        var r1 = await _server.Call("delegate_task", new() { ["task_description"] = "Part one" });
        var taskId = TestDelegationServer.Parse(r1).GetProperty("task_id").GetInt32();

        // Second delegation reuses the same task id.
        _server.Llm.Enqueue(FakeLlm.Complete("Second part done."));
        var r2 = await _server.Call("delegate_task", new() { ["task_description"] = "Part two", ["task_id"] = taskId });

        Assert.False(r2.IsError);
        var j2 = TestDelegationServer.Parse(r2);
        Assert.Equal(taskId, j2.GetProperty("task_id").GetInt32()); // same session id
        Assert.Equal("completed", j2.GetProperty("status").GetString());

        // The second LLM request must contain the full history from part one.
        var secondRequest = _server.Llm.Requests.Last();
        Assert.Contains(secondRequest.Messages, m => m.Role == "tool" && (m.Content ?? "").Contains("First part done."));
        Assert.Contains(secondRequest.Messages, m => m.Role == "user" && m.Content!.Contains("Part two"));
    }

    [Fact]
    public async Task DelegateTask_WithUnknownTaskId_ReturnsError()
    {
        var r = await _server.Call("delegate_task", new() { ["task_description"] = "x", ["task_id"] = 987654 });
        Assert.True(r.IsError);
        Assert.Contains("Unknown task_id", TextOf(r));
    }

    [Fact]
    public async Task DelegateTask_WithNonCompletedTaskId_ReturnsError()
    {
        // Start an async task that stalls in a blocking external tool (stays running).
        _server.External.BlockUntilCancelled = true;
        _server.Llm.Enqueue(FakeLlm.ToolCall("local_read_file", new { path = "/x" }));
        var d = await _server.Call("delegate_task_async", new() { ["task_description"] = "stall" });
        var taskId = TestDelegationServer.Parse(d).GetProperty("task_id").GetInt32();
        await _server.WaitForStatusAsync(taskId, "running");

        var r = await _server.Call("delegate_task", new() { ["task_description"] = "continue stalled", ["task_id"] = taskId });
        Assert.True(r.IsError);
        Assert.Contains("Only completed tasks can be resumed", TextOf(r));

        await _server.Call("stop_task", new() { ["task_id"] = taskId });
    }

    // ------------------------------------------------------------------
    // describe_capabilities
    // ------------------------------------------------------------------

    [Fact]
    public async Task DescribeCapabilities_ReturnsLocalLlmDescriptionWithFullToolSet()
    {
        _server.Llm.Enqueue(FakeLlm.Text("I can read files (local_read_file), get the time (global_get_time) and complete/clarify tasks."));

        var r = await _server.Call("describe_capabilities");
        Assert.False(r.IsError);
        var json = TestDelegationServer.Parse(r);
        Assert.Contains("read files", json.GetProperty("capabilities").GetString()!);

        // The LLM must have been given the local-scoped tool set (local + global, NOT frontier-only).
        var request = _server.Llm.Requests.Last();
        var toolNames = request.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("complete_task", toolNames);
        Assert.Contains("clarify_task", toolNames);
        Assert.Contains("report_progress", toolNames);
        Assert.Contains("local_read_file", toolNames);
        Assert.Contains("global_get_time", toolNames);
        Assert.DoesNotContain("frontier_search_web", toolNames); // frontier-scoped tools are hidden from the local LLM
    }

    // ------------------------------------------------------------------
    // external proxy tools (frontier-facing)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FrontierSeesFrontierAndGlobalProxies_LocalToolsAreHidden()
    {
        var client = _server.Client;
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToList();

        // Native tools.
        foreach (var native in new[] { "delegate_task", "delegate_task_async", "pending_tasks", "stop_task", "task_status", "clarify", "describe_capabilities" })
            Assert.Contains(native, names);

        // Proxies: frontier + global visible; local hidden.
        Assert.Contains("frontier_search_web", names);
        Assert.Contains("global_get_time", names);
        Assert.DoesNotContain("local_read_file", names);
    }

    [Fact]
    public async Task FrontierProxyTool_ForwardsArgsAndReturnsResult()
    {
        var r = await _server.Call("frontier_search_web", new() { ["query"] = "cats" });
        Assert.False(r.IsError);
        Assert.Contains("ok:frontier_search_web", TextOf(r));

        var invocation = _server.External.Invocations.Single(i => i.PrefixedName == "frontier_search_web");
        Assert.Equal("cats", invocation.Args!["query"].GetString());
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static string TextOf(CallToolResult r) =>
        string.Join("|", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
}

/// <summary>LLM that always throws, to test failure handling.</summary>
public sealed class ThrowingLlm : IChatLlm
{
    public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition>? tools, CancellationToken cancellationToken) =>
        throw new LlmException("boom: endpoint unreachable");
}
