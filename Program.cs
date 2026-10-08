using LocalDelegationMcp.Agent;
using LocalDelegationMcp.Config;
using LocalDelegationMcp.Llm;
using LocalDelegationMcp.Mcp;
using LocalDelegationMcp.Tasks;
using LocalDelegationMcp.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using System.Threading.Channels;

// ---------------------------------------------------------------------------
// 1. Load configuration (delegation.config.json next to the executable, or an
//    explicit path passed as the first CLI argument).
// ---------------------------------------------------------------------------
var configPath = args.Length > 0 ? args[0] : null;
AppConfig config;
using (var bootstrapLog = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information)))
{
    config = AppConfig.Load(configPath, bootstrapLog.CreateLogger("Config"));
}

// ---------------------------------------------------------------------------
// 2. Core components (manual wiring; singletons shared by the MCP tools).
// ---------------------------------------------------------------------------
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("DelegationMcp");

log.LogInformation("Starting Local Delegation MCP server on port {Port} ...", config.ListenPort);

var external = new ExternalMcpManager(loggerFactory.CreateLogger<ExternalMcpManager>());
await external.ConnectAllAsync(config.ExternalMcpServers);

var registry = new TaskRegistry(config.Agent.MaxConcurrentTasks);
using var llm = new OpenAiCompatibleClient(
    config.OpenAi,
    config.Agent.LlmRequestTimeoutSeconds,
    loggerFactory.CreateLogger<OpenAiCompatibleClient>());
var runner = new AgentRunner(llm, external, config.Agent, loggerFactory.CreateLogger<AgentRunner>());

// ---------------------------------------------------------------------------
// 3. MCP server for the frontier model: native delegation tools + external
//    servers scoped 'frontier' or 'global'.
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://*:{config.ListenPort}");
builder.Logging.ClearProviders(); // our components log via their own factory; keep host quiet
builder.Services.AddSingleton(registry);

// Shared provider for tool options (handlers are closures; no DI resolution needed).
var toolServices = EmptyServiceProvider.Instance;

var nativeTools = FrontierTools.BuildAll(registry, runner, external, toolServices);

var proxyTools = external.ToolsForScope(McpScope.Frontier)
    .Select(t => new ExternalProxyTool(external, t))
    .ToList();

var allTools = nativeTools.Concat(proxyTools).ToList();
log.LogInformation("Exposing {Count} tool(s) to the frontier model ({Native} native + {Proxies} proxied).",
    allTools.Count, nativeTools.Count, proxyTools.Count);

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation { Name = "local-delegation-mcp", Version = "1.0.0" };
})
.WithHttpTransport(httpOptions =>
{
    // Stateless: every request gets the same fixed tool collection; all mutable state
    // (task sessions) lives in the TaskRegistry singleton, not in MCP sessions.
    httpOptions.SessionMode = HttpServerSessionMode.Stateless;
})
.WithTools(allTools);

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapMcp("/mcp");

app.MapGet("/api/tasks", (HttpContext context, TaskRegistry tasks) =>
    IsLoopback(context)
        ? Results.Ok(tasks.All()
            .Select(session => session.Snapshot())
            .OrderByDescending(session => session.UpdatedAtUtc)
            .Select(session => new TaskListItem(session.TaskId, session.Description, session.Status, session.CreatedAtUtc, session.UpdatedAtUtc))
            .ToList())
        : Results.StatusCode(StatusCodes.Status403Forbidden));

app.MapGet("/api/tasks/{taskId:int}", (HttpContext context, int taskId, TaskRegistry tasks) =>
    !IsLoopback(context)
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : tasks.TryGet(taskId, out var session) && session is not null
            ? Results.Ok(session.Snapshot())
            : Results.NotFound(new { error = $"Task {taskId} does not exist." }));

app.MapGet("/api/events", async (HttpContext context, TaskRegistry tasks) =>
{
    if (!IsLoopback(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var cancellationToken = context.RequestAborted;
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    using var subscription = tasks.SubscribeChanges();
    await context.Response.WriteAsync("event: ready\ndata: {}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await subscription.Reader.ReadAsync(waitCts.Token);
            while (subscription.Reader.TryRead(out _)) { }
            await context.Response.WriteAsync("event: tasks_changed\ndata: {}\n\n", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await context.Response.WriteAsync(": keepalive\n\n", cancellationToken);
        }
        catch (ChannelClosedException)
        {
            break;
        }

        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

// Lightweight health/info endpoint for operators.
app.MapGet("/health", () => Results.Json(new
{
    service = "local-delegation-mcp",
    mcp_endpoint = "/mcp",
    web_ui = "/",
    listen_port = config.ListenPort,
    local_llm = new { base_url = config.OpenAi.BaseUrl, model = config.OpenAi.Model },
    external_servers = external.Servers.Select(s => new { s.Name, scope = s.Scope.ToString().ToLowerInvariant(), healthy = s.Healthy, tool_count = s.ToolCount, error = s.Error })
}));

app.Run();

// ---------------------------------------------------------------------------
// Cleanup on shutdown.
// ---------------------------------------------------------------------------
registry.Dispose();
await external.DisposeAsync();
loggerFactory.Dispose();

static bool IsLoopback(HttpContext context) =>
    context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);

sealed record TaskListItem(int TaskId, string Description, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();
    public object? GetService(Type serviceType) => null;
}
