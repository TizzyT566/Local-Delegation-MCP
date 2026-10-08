# Local Delegation MCP Server

## Goal and architecture

This .NET 10 MCP server lets a frontier model delegate work to a local OpenAI-compatible chat-completions model. The frontier connects to the server over stateless Streamable HTTP at `/mcp`. The server drives the local model with an in-process tool-calling loop; no second MCP transport is needed for the local model. A browser console at `/` lists task sessions and streams conversation updates over server-sent events; its task API is restricted to loopback clients.

External MCP servers can connect over stdio or Streamable HTTP. Their tools are proxied with a `{serverName}_{toolName}` name and scoped to `frontier`, `local`, or `global` consumers.

## Implementation status

- [x] Project scaffold and ModelContextProtocol.AspNetCore 2.2.0 dependency.
- [x] Configuration POCOs, defaults, JSON loading, and validation.
- [x] OpenAI-compatible chat client, tool-call serialization, timeout, and endpoint error handling.
- [x] Task sessions, in-memory registry, configurable active-task limit, cancellation, clarification, and completed-session resume.
- [x] External MCP stdio and HTTP connections, scoped proxy tools, connection failure isolation, and client cleanup.
- [x] Agent loop with completion, clarification, and progress-reporting tools, sequential external tool calls, implicit text completion, iteration limit, cancellation, and capability description.
- [x] Seven frontier-facing delegation and task management tools.
- [x] Stateless Streamable HTTP host, `/mcp` endpoint, health endpoint, and sample configuration copied to build output.
- [x] Browser task console with a task sidebar, conversation view, progress/status updates, and server-sent event refreshes.
- [x] README with setup, configuration, tool behavior, and build/test instructions.
- [x] xUnit coverage using a fake LLM and in-memory MCP client/server transport.
- [x] .NET 10 build and test suite verified.

## Verified SDK notes

These implementation notes came from the `api-probe` project and are retained as a reference:

1. `McpServer.Create(transport, options, serviceProvider)` creates the server but does not start processing; call `RunAsync(ct)`.
2. `StreamClientTransport(serverInput, serverOutput)` takes the stream the client writes to, then the stream it reads from.
3. Typed tool handler parameters with default values produce optional schemas. Prefer non-nullable value parameters with defaults when omitted arguments should bind safely.
4. Replacing `ProtocolTool.InputSchema` on a typed handler can break argument binding because the SDK binds using C# parameter names.
5. A proxy for arbitrary remote schemas can subclass `McpServerTool`, expose a `ProtocolTool`, and read raw arguments from `RequestContext<CallToolRequestParams>`.
6. Tool handlers can return `CallToolResult`, including an error result.
7. A channel-backed stream pair works with the client and server transports for tests.

## Frontier tool contracts

| Tool | Behavior |
|---|---|
| `delegate_task(task_description, completion_criteria?, task_id?)` | Runs until completed, waiting for clarification, stopped, or failed. Only a completed task can be resumed with its id. |
| `delegate_task_async(...)` | Starts a task in the background and returns its id. |
| `pending_tasks()` | Lists running and clarification-waiting tasks. |
| `task_status(task_id)` | Returns the status plus the latest progress note, result, clarification question, or failure information when available. |
| `stop_task(task_id)` | Stops a running or clarification-waiting task. Completed, failed, and stopped tasks stay unchanged. |
| `clarify(task_id, clarification_description)` | Atomically accepts one answer for a waiting task and resumes it in the background. |
| `describe_capabilities()` | Asks the local model to describe its capabilities with its local/global tools in context. |

The local model receives `complete_task`, `clarify_task`, and `report_progress` plus external tools in `local` and `global` scopes. Progress notes are visible to the frontier through `task_status` and `pending_tasks`. Conversation history is retained when a completed session is resumed. Session state is in memory and is cleared when the server exits.

## Configuration reference

The sample `delegation.config.json` defines:

```json
{
  "listenPort": 8765,
  "openAi": { "baseUrl": "http://localhost:1234/v1", "apiKey": "", "model": "local-model" },
  "agent": { "maxIterationsPerTask": 25, "maxConcurrentTasks": 4, "llmRequestTimeoutSeconds": 300, "systemPrompt": "..." },
  "externalMcpServers": []
}
```

Stdio external-server entries use `command`, optional `args`, `workingDirectory`, and `environmentVariables`. HTTP entries use `url` and optional `headers`. Failed external-server connections are logged and isolated from other configured servers.

## Verification scenarios

The tests cover synchronous and asynchronous delegation, completion, implicit completion, clarification and resume, progress reporting, tool parameter descriptions, cancellation, task status/listing, errors, active-task capacity, completed-session continuation, capability description, external-tool scope and argument forwarding, and the iteration limit.
