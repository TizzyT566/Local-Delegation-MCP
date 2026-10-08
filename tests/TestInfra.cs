using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using LocalDelegationMcp.Agent;
using LocalDelegationMcp.Config;
using LocalDelegationMcp.Llm;
using LocalDelegationMcp.Mcp;
using LocalDelegationMcp.Tasks;
using LocalDelegationMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalDelegationMcp.Tests;

// ---------------------------------------------------------------------------
// In-memory MCP transport pair (channel-backed streams). Verified working with
// StreamServerTransport + StreamClientTransport in the api-probe project.
// ---------------------------------------------------------------------------
public sealed class ChannelReaderStream : Stream
{
    private readonly ChannelReader<byte> _reader;
    public ChannelReaderStream(ChannelReader<byte> reader) => _reader = reader;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true; // transports wrap both streams in StreamReader/Writer regardless of role
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        if (!_reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return 0; // completed and drained
        var n = 0;
        while (n < count && _reader.TryRead(out var b)) buffer[offset + n++] = b;
        return n;
    }
    public override void Write(byte[] buffer, int offset, int count) { } // unused direction: discard
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        if (!await _reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
        var n = 0;
        while (n < buffer.Length && _reader.TryRead(out var b)) buffer.Span[n++] = b;
        return n;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask; // unused direction: discard
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class ChannelWriterStream : Stream
{
    private readonly ChannelWriter<byte> _writer;
    public ChannelWriterStream(ChannelWriter<byte> writer) => _writer = writer;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0; // unused direction: EOF
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(0); // unused direction: EOF
    public override void Write(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++) _writer.WriteAsync(buffer[offset + i]).AsTask().GetAwaiter().GetResult();
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var arr = buffer.ToArray(); // copy: span cannot be preserved across await
        foreach (var b in arr) await _writer.WriteAsync(b, ct).ConfigureAwait(false);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public static class MemTransport
{
    /// <summary>Returns (serverInput, serverOutput, clientWrite /* we write */, clientRead /* we read */).</summary>
    public static (Stream si, Stream so, Stream cw, Stream cr) CreatePair()
    {
        var c2s = Channel.CreateUnbounded<byte>(); // client -> server
        var s2c = Channel.CreateUnbounded<byte>(); // server -> client
        return (new ChannelReaderStream(c2s.Reader), new ChannelWriterStream(s2c.Writer),
                new ChannelWriterStream(c2s.Writer), new ChannelReaderStream(s2c.Reader));
    }
}

// ---------------------------------------------------------------------------
// Scripted fake LLM.
// ---------------------------------------------------------------------------
public sealed class FakeLlm : IChatLlm
{
    private readonly Queue<LlmResponse> _script = [];
    private readonly SemaphoreSlim _gate = new(0);
    public List<(IReadOnlyList<LlmMessage> Messages, IReadOnlyList<LlmToolDefinition>? Tools)> Requests { get; } = [];

    /// <summary>How long ChatAsync waits for a scripted response before failing (guards against under-scripted tests).</summary>
    public TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public void Enqueue(LlmResponse response)
    {
        lock (_script) _script.Enqueue(response);
        _gate.Release();
    }

    /// <summary>Returns the next scripted response, waiting if the test has not enqueued it yet (avoids races with background agent loops).</summary>
    public async Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition>? tools, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ScriptTimeout);

        try
        {
            await _gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // session stopped — let the agent loop see cancellation
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("FakeLlm: no scripted response available within " + ScriptTimeout + " (test under-scripted?).", ex);
        }

        lock (_script)
        {
            Requests.Add((messages.ToList(), tools?.ToList()));
            return _script.Dequeue();
        }
    }

    // --- response builders (arguments as JSON strings, like real OpenAI endpoints) ---

    public static LlmResponse Complete(string description) => ToolCall("complete_task", new { task_completion_description = description });

    public static LlmResponse Clarify(string question) => ToolCall("clarify_task", new { question });

    public static LlmResponse ToolCall(string name, object? argsObject)
    {
        var callId = "call_" + Guid.NewGuid().ToString("N")[..8];
        return new LlmResponse
        {
            Content = null,
            ToolCalls = [JsonSerializer.SerializeToElement(new
            {
                id = callId,
                type = "function",
                function = new { name, arguments = JsonSerializer.Serialize(argsObject) } // string form, as OpenAI returns it
            })]
        };
    }

    public static LlmResponse Text(string content) => new() { Content = content, ToolCalls = [] };
}

// ---------------------------------------------------------------------------
// Fake external MCP tool provider.
// ---------------------------------------------------------------------------
public sealed class FakeExternalTools : IExternalTools
{
    private readonly List<ExternalTool> _tools;
    public List<(string PrefixedName, IReadOnlyDictionary<string, JsonElement>? Args)> Invocations { get; } = [];

    /// <summary>Default reply for any invocation.</summary>
    public Func<ExternalTool, string> DefaultResult { get; set; } = t => $"ok:{t.PrefixedName}";

    /// <summary>When true, invocations block until the token is cancelled (simulates a long-running external tool).</summary>
    public bool BlockUntilCancelled { get; set; }

    /// <summary>Creates a fake tool set. Server names "local"/"frontier"/"global" determine scope in ToolsForScope.</summary>
    public FakeExternalTools(params (string Server, string ToolName, string Desc)[] tools)
    {
        _tools = tools.Select(t => new ExternalTool(
            t.Server, t.ToolName, $"{Sanitize(t.Server)}_{t.ToolName}", t.Desc,
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() }))).ToList();
    }

    public FakeExternalTools(IEnumerable<ExternalTool> tools) => _tools = tools.ToList();

    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    public IReadOnlyList<ExternalTool> ToolsForScope(McpScope scope) =>
        _tools.Where(t => scope == McpScope.Frontier
            ? t.ServerName is "frontier" or "global"
            : t.ServerName is "local" or "global").ToList();

    public async Task<CallToolResult> InvokeAsync(ExternalTool tool, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        lock (Invocations) Invocations.Add((tool.PrefixedName, args));

        if (BlockUntilCancelled)
            await Task.Delay(Timeout.Infinite, ct); // blocks until the session is stopped

        return new CallToolResult { Content = [new TextContentBlock { Text = DefaultResult(tool) }], IsError = false };
    }

    public IReadOnlyList<ServerInfo> Servers => [];
}

// ---------------------------------------------------------------------------
// Full test server: real McpServer + real McpClient over in-memory transport,
// with the production FrontierTools wired to fakes.
// ---------------------------------------------------------------------------
public sealed class TestDelegationServer : IAsyncDisposable
{
    public TaskRegistry Registry { get; } = new();
    public FakeLlm Llm { get; } = new();
    public FakeExternalTools External { get; }
    public AgentRunner Runner { get; }
    public McpClient Client { get; private set; } = null!;

    private readonly McpServer _server;
    private readonly CancellationTokenSource _runCts = new();
    private readonly Stream _clientWrite;
    private readonly Stream _clientRead;

    /// <summary>Exception from the server's RunAsync loop, if it died (e.g. DI failure at startup).</summary>
    public Exception? ServerError { get; private set; }
    public Task RunTask { get; private set; } = null!;

    public TestDelegationServer(FakeExternalTools? external = null)
    {
        External = external ?? new FakeExternalTools(
            ("local", "read_file", "Reads a file."),
            ("frontier", "search_web", "Searches the web."),
            ("global", "get_time", "Gets current time."));

        var options = new AgentOptions { MaxIterationsPerTask = 10 };
        Runner = new AgentRunner(Llm, External, options, NullLogger<AgentRunner>.Instance);

        var services = new ServiceCollection().BuildServiceProvider();
        var tools = FrontierTools.BuildAll(Registry, Runner, External, services)
            .Concat(External.ToolsForScope(McpScope.Frontier).Select(t => new ExternalProxyTool(External, t)))
            .ToList();

        var (si, so, cw, cr) = MemTransport.CreatePair();
        _clientWrite = cw;
        _clientRead = cr;
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var t in tools) collection.Add(t);

        var serverOptions = new McpServerOptions { ScopeRequests = false, ToolCollection = collection };

        _server = McpServer.Create(
            new StreamServerTransport(si, so),
            serverOptions,
            serviceProvider: services);

        RunTask = Task.Run(async () =>
        {
            try { await _server.RunAsync(_runCts.Token); }
            catch (Exception ex) { ServerError = ex; }
        });
    }

    public async Task ConnectClientAsync()
    {
        try
        {
            Client = await McpClient.CreateAsync(new StreamClientTransport(_clientWrite, _clientRead), cancellationToken: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"MCP client initialize timed out. Server RunAsync error: {ServerError}", ServerError ?? ex);
        }
    }

    public async Task<CallToolResult> Call(string tool, Dictionary<string, object?>? args = null) =>
        await Client.CallToolAsync(tool, args ?? new Dictionary<string, object?>(), cancellationToken: CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Polls task_status until it reaches a specific status (or timeout).</summary>
    public async Task<CallToolResult> WaitForStatusAsync(int taskId, string expected, int timeoutMs = 5_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = await Call("task_status", new() { ["task_id"] = taskId });
            if (Parse(r).GetProperty("status").GetString() == expected) return r;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Task {taskId} did not reach '{expected}' within {timeoutMs}ms.");
    }

    /// <summary>Polls task_status until the task leaves 'running' (or timeout).</summary>
    public async Task<CallToolResult> WaitForTerminalAsync(int taskId, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = await Call("task_status", new() { ["task_id"] = taskId });
            var json = Parse(r);
            var status = json.GetProperty("status").GetString();
            if (status is not ("running")) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Task {taskId} still running after {timeoutMs}ms.");
    }

    public static JsonElement Parse(CallToolResult result) =>
        JsonSerializer.Deserialize<JsonElement>(result.Content.OfType<TextContentBlock>().First().Text)!;

    public async ValueTask DisposeAsync()
    {
        _runCts.Cancel();
        try { await RunTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* server shutdown not critical */ }
        await Client.DisposeAsync();
        Registry.Dispose();
    }
}
