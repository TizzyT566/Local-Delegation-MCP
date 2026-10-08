using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using LocalDelegationMcp.Config;

namespace LocalDelegationMcp.Mcp;

/// <summary>A tool from an external MCP server, addressable by its prefixed name.</summary>
public sealed record ExternalTool(
    string ServerName,
    string OriginalName,
    string PrefixedName,
    string Description,
    JsonElement InputSchema);

/// <summary>Abstraction over the set of connected external MCP servers (implemented by ExternalMcpManager; faked in tests).</summary>
public interface IExternalTools
{
    /// <summary>Tools visible to the given consumer: frontier sees frontier+global; local sees local+global.</summary>
    IReadOnlyList<ExternalTool> ToolsForScope(McpScope scope);

    /// <summary>Invokes an external tool, forwarding arguments verbatim (original name is used on the wire).</summary>
    Task<CallToolResult> InvokeAsync(ExternalTool tool, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct);

    /// <summary>All connected servers (for diagnostics / health endpoint).</summary>
    IReadOnlyList<ServerInfo> Servers { get; }
}

public sealed record ServerInfo(string Name, McpScope Scope, int ToolCount, bool Healthy, string? Error);

/// <summary>Connects to configured external MCP servers and exposes their tools (prefixed) per scope.</summary>
public sealed class ExternalMcpManager : IExternalTools, IAsyncDisposable
{
    private readonly List<ConnectedServer> _servers = [];
    private readonly ILogger<ExternalMcpManager> _logger;

    public ExternalMcpManager(ILogger<ExternalMcpManager> logger) => _logger = logger;

    private sealed record ConnectedServer(string Name, McpScope Scope, McpClient? Client, IReadOnlyList<ExternalTool> Tools, bool Healthy, string? Error);

    public IReadOnlyList<ServerInfo> Servers => _servers.Select(s => new ServerInfo(s.Name, s.Scope, s.Tools.Count, s.Healthy, s.Error)).ToList();

    /// <summary>Connects to every configured server. Failures are isolated: a broken server logs an error and contributes no tools.</summary>
    public async Task ConnectAllAsync(IReadOnlyList<ExternalMcpServerConfig> configs, CancellationToken ct = default)
    {
        foreach (var cfg in configs)
        {
            McpClient? client = null;
            try
            {
                client = await CreateClientAsync(cfg);
                var listed = await client.ListToolsAsync(cancellationToken: ct);

                var prefix = SanitizePrefix(cfg.Name!);
                var tools = listed.Select(t => new ExternalTool(
                    cfg.Name!,
                    t.ProtocolTool.Name,
                    TruncateName($"{prefix}_{t.ProtocolTool.Name}"),
                    t.ProtocolTool.Description ?? "",
                    t.ProtocolTool.InputSchema.Clone())).ToList();

                _servers.Add(new ConnectedServer(cfg.Name!, cfg.Scope, client, tools, true, null));
                client = null;
                _logger.LogInformation("Connected external MCP server '{Name}' ({Scope}) with {Count} tool(s).", cfg.Name, cfg.Scope, tools.Count);
            }
            catch (Exception ex)
            {
                if (client is not null)
                    await client.DisposeAsync();
                _servers.Add(new ConnectedServer(cfg.Name!, cfg.Scope, null, Array.Empty<ExternalTool>(), false, ex.Message));
                _logger.LogError(ex, "Failed to connect external MCP server '{Name}' — its tools will not be available.", cfg.Name);
            }
        }
    }

    private static async Task<McpClient> CreateClientAsync(ExternalMcpServerConfig cfg)
    {
        var transport = (cfg.Transport ?? "stdio").Trim().ToLowerInvariant();

        if (transport is "http" or "streamablehttp" or "streamable-http")
        {
            return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(cfg.Url!),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = cfg.Name,
                AdditionalHeaders = cfg.Headers.Count > 0 ? cfg.Headers : null
            }));
        }

        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = cfg.Command!,
            Arguments = cfg.Args,
            Name = cfg.Name,
            WorkingDirectory = cfg.WorkingDirectory,
            EnvironmentVariables = cfg.EnvironmentVariables.Count > 0
                ? cfg.EnvironmentVariables.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                : null,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        }));
    }

    public IReadOnlyList<ExternalTool> ToolsForScope(McpScope scope) =>
        _servers
            .Where(s => s.Healthy && (scope == McpScope.Frontier
                ? s.Scope is McpScope.Frontier or McpScope.Global
                : s.Scope is McpScope.Local or McpScope.Global))
            .SelectMany(s => s.Tools)
            .ToList();

    public async Task<CallToolResult> InvokeAsync(ExternalTool tool, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var entry = _servers
            .Where(s => s.Healthy)
            .SelectMany(s => s.Tools.Select(t => (Tool: t, s.Client)))
            .FirstOrDefault(t => t.Tool.PrefixedName == tool.PrefixedName);

        if (entry.Tool is null || entry.Client is null)
            throw new InvalidOperationException($"External tool '{tool.PrefixedName}' is not connected.");

        var dict = new Dictionary<string, object?>();
        if (args != null)
            foreach (var kv in args) dict[kv.Key] = kv.Value;

        return await entry.Client.CallToolAsync(entry.Tool.OriginalName, dict.Count > 0 ? dict : null, cancellationToken: ct);
    }

    /// <summary>Flattens a CallToolResult's content blocks into a single text string.</summary>
    public static string FlattenContent(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock tcb:
                    parts.Add(tcb.Text);
                    break;
                default:
                    parts.Add($"[{block.GetType().Name} content]");
                    break;
            }
        }

        var flattened = string.Join("\n", parts).Trim();
        if (result.IsError == true && !flattened.StartsWith("ERROR"))
            flattened = "ERROR: " + (string.IsNullOrEmpty(flattened) ? "the tool reported an error" : flattened);
        return flattened;
    }

    private static string SanitizePrefix(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    // MCP tool names must match ^[a-zA-Z0-9_-]{1,64}$
    private static string TruncateName(string name) => name.Length <= 64 ? name : name[..64];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _servers.Select(s => s.Client).Where(c => c is not null).Distinct())
            await client!.DisposeAsync();
    }
}

/// <summary>An McpServerTool that forwards calls to an external MCP tool, exposing the remote schema under a prefixed name.</summary>
public sealed class ExternalProxyTool : McpServerTool
{
    private readonly Tool _protocolTool;
    private readonly IExternalTools _invoker;
    private readonly ExternalTool _tool;

    public ExternalProxyTool(IExternalTools invoker, ExternalTool tool)
    {
        _invoker = invoker;
        _tool = tool;
        // Clone the remote protocol tool so we can rename it without mutating shared state.
        _protocolTool = new Tool
        {
            Name = tool.PrefixedName,
            Description = $"[{tool.ServerName}] " + tool.Description,
            InputSchema = tool.InputSchema.Clone()
        };
    }

    public override Tool ProtocolTool => _protocolTool;
    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        try
        {
            return await _invoker.InvokeAsync(_tool, request.Params.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value), cancellationToken);
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"ERROR: external MCP server '{_tool.ServerName}' failed: {ex.Message}" }],
                IsError = true
            };
        }
    }
}
