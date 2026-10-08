using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalDelegationMcp.Config;

/// <summary>Root configuration for the delegation MCP server. Loaded from delegation.config.json next to the executable.</summary>
public sealed class AppConfig
{
    /// <summary>TCP port the MCP (Streamable HTTP) endpoint listens on for the frontier model.</summary>
    public int ListenPort { get; set; } = 8765;

    public OpenAiOptions OpenAi { get; set; } = new();

    public AgentOptions Agent { get; set; } = new();

    /// <summary>External MCP servers to connect to and expose (scoped frontier/local/global).</summary>
    public List<ExternalMcpServerConfig> ExternalMcpServers { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Loads config from an explicit path, or delegation.config.json alongside the executable. Missing file yields defaults.</summary>
    public static AppConfig Load(string? explicitPath, ILogger logger)
    {
        var path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "delegation.config.json");

        if (!File.Exists(path))
        {
            logger.LogWarning("Config file not found at '{Path}'. Using built-in defaults.", path);
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            config.Validate();
            logger.LogInformation("Loaded configuration from '{Path}'.", path);
            return config;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to parse config file at '{path}': {ex.Message}", ex);
        }
    }

    public void Validate()
    {
        if (ListenPort is < 1 or > 65535)
            throw new InvalidOperationException($"listenPort must be between 1 and 65535, got {ListenPort}.");

        if (OpenAi is null)
            throw new InvalidOperationException("openAi configuration is required.");
        if (Agent is null)
            throw new InvalidOperationException("agent configuration is required.");
        if (ExternalMcpServers is null)
            throw new InvalidOperationException("externalMcpServers must be an array.");

        if (string.IsNullOrWhiteSpace(OpenAi.BaseUrl))
            throw new InvalidOperationException("openAi.baseUrl is required.");

        if (!Uri.TryCreate(OpenAi.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("openAi.baseUrl must be an absolute HTTP or HTTPS URL.");

        if (string.IsNullOrWhiteSpace(OpenAi.Model))
            throw new InvalidOperationException("openAi.model is required.");

        if (Agent.MaxIterationsPerTask < 1)
            throw new InvalidOperationException("agent.maxIterationsPerTask must be >= 1.");

        if (Agent.MaxConcurrentTasks < 1)
            throw new InvalidOperationException("agent.maxConcurrentTasks must be >= 1.");

        if (Agent.LlmRequestTimeoutSeconds < 1)
            throw new InvalidOperationException("agent.llmRequestTimeoutSeconds must be >= 1.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in ExternalMcpServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException("Every entry in externalMcpServers needs a 'name'.");

            if (!seenNames.Add(server.Name!))
                throw new InvalidOperationException($"Duplicate external MCP server name '{server.Name}'. Names are used as tool prefixes and must be unique.");

            if (!Enum.IsDefined(server.Scope))
                throw new InvalidOperationException($"External MCP server '{server.Name}' has an invalid scope.");

            var transport = (server.Transport ?? "stdio").Trim().ToLowerInvariant();
            switch (transport)
            {
                case "stdio":
                    if (string.IsNullOrWhiteSpace(server.Command))
                        throw new InvalidOperationException($"External MCP server '{server.Name}' uses stdio transport but has no 'command'.");
                    break;
                case "http" or "streamablehttp" or "streamable-http":
                    if (string.IsNullOrWhiteSpace(server.Url) || !Uri.TryCreate(server.Url, UriKind.Absolute, out _))
                        throw new InvalidOperationException($"External MCP server '{server.Name}' uses http transport but has no valid 'url'.");
                    break;
                default:
                    throw new InvalidOperationException($"External MCP server '{server.Name}' has unknown transport '{server.Transport}' (expected 'stdio' or 'http').");
            }
        }
    }
}

/// <summary>OpenAI-compatible inference endpoint (LM Studio, llama-server, ...).</summary>
public sealed class OpenAiOptions
{
    /// <summary>Base URL of the chat completions API, e.g. http://localhost:1234/v1</summary>
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";

    /// <summary>API key (many local servers accept any non-empty value).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Model name to request.</summary>
    public string Model { get; set; } = "local-model";
}

/// <summary>Tuning for the in-process agent loop that drives the local LLM.</summary>
public sealed class AgentOptions
{
    /// <summary>Maximum number of running or clarification-waiting tasks held at once.</summary>
    public int MaxConcurrentTasks { get; set; } = 4;

    /// <summary>Maximum chat-completions round trips per task before it is failed.</summary>
    public int MaxIterationsPerTask { get; set; } = 25;

    /// <summary>Timeout for a single LLM request, in seconds.</summary>
    public int LlmRequestTimeoutSeconds { get; set; } = 300;

    /// <summary>System prompt given to the local LLM at the start of every task session.</summary>
    public string SystemPrompt { get; set; } =
        "You are a capable assistant working on delegated tasks from a parent model. " +
        "Use your available tools to accomplish the work step by step. " +
        "For longer tasks, call report_progress with a concise note at meaningful milestones so the parent can see what is happening; avoid repetitive updates. " +
        "When you have finished, call complete_task with a clear summary of what you did and the result. " +
        "If you lack information needed to proceed correctly, call clarify_task with a specific question.";
}

/// <summary>Who can see an external MCP server's tools.</summary>
public enum McpScope
{
    /// <summary>Only the frontier (parent) model.</summary>
    Frontier,

    /// <summary>Only the local LLM.</summary>
    Local,

    /// <summary>Both the frontier model and the local LLM.</summary>
    Global
}

/// <summary>One external MCP server to connect to at startup.</summary>
public sealed class ExternalMcpServerConfig
{
    /// <summary>Unique name; used as the prefix for all of this server's tools (e.g. "filesystem_read_file").</summary>
    public string Name { get; set; } = "";

    /// <summary>frontier | local | global.</summary>
    public McpScope Scope { get; set; } = McpScope.Local;

    /// <summary>stdio (default) or http (Streamable HTTP).</summary>
    public string Transport { get; set; } = "stdio";

    // --- stdio transport ---
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    // --- http transport ---
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}
