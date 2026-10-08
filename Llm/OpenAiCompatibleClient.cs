using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalDelegationMcp.Config;

namespace LocalDelegationMcp.Llm;

/// <summary>Minimal OpenAI-compatible chat completions client (works with LM Studio, llama-server, vLLM, etc.).</summary>
public sealed class OpenAiCompatibleClient : IChatLlm, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<OpenAiCompatibleClient> _logger;

    public OpenAiCompatibleClient(OpenAiOptions options, int timeoutSeconds, ILogger<OpenAiCompatibleClient> logger)
    {
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/chat/completions");
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "no-key" : options.ApiKey;
        _model = options.Model;
        _logger = logger;

        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan // per-request timeouts handled via CTS
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        _timeoutSeconds = timeoutSeconds;
    }

    private readonly int _timeoutSeconds;

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(SerializeMessage).ToList(),
            ["stream"] = false
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema
                }
            }).ToList();
        }

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
                throw new LlmException($"LLM endpoint returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Standard OpenAI shape: choices[0].message.{content, tool_calls}
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                string? content = null;
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();

                var toolCalls = new List<JsonElement>();
                if (message.TryGetProperty("tool_calls", out var tcs))
                    foreach (var tc in tcs.EnumerateArray())
                        toolCalls.Add(tc.Clone());

                return new LlmResponse { Content = content, ToolCalls = toolCalls };
            }

            throw new LlmException($"Unexpected response shape from LLM endpoint: {Truncate(body)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"LLM request timed out after {_timeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Could not reach the LLM endpoint: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new LlmException($"LLM endpoint returned invalid JSON: {ex.Message}");
        }
    }

    private static object? SerializeMessage(LlmMessage m) => m.Role switch
    {
        "system" or "user" => new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content },
        "assistant" => BuildAssistant(m),
        "tool" => new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["tool_call_id"] = m.ToolCallId,
            ["name"] = m.Name,
            ["content"] = m.Content
        },
        _ => throw new InvalidOperationException($"Unknown role '{m.Role}'.")
    };

    private static Dictionary<string, object?> BuildAssistant(LlmMessage m)
    {
        var dict = new Dictionary<string, object?> { ["role"] = "assistant" };
        // OpenAI requires content to be present (null is fine when tool_calls are used).
        dict["content"] = m.Content;
        if (m.ToolCalls is { Count: > 0 })
            dict["tool_calls"] = m.ToolCalls;
        return dict;
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>Exception for failures talking to the LLM endpoint.</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}
