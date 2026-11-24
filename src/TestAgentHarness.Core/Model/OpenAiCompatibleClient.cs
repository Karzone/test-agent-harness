using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestAgentHarness.Model;

/// <summary>
/// Talks to any endpoint that implements the OpenAI "chat completions" API with
/// tool calling: OpenAI, Azure OpenAI (v1 endpoint), OpenRouter and most gateways.
/// Swap models by changing one string.
/// </summary>
public sealed class OpenAiCompatibleClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OpenAiCompatibleClient(string baseUrl, string apiKey, string model, HttpClient? http = null)
    {
        _model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        // Azure OpenAI takes an API key in its own header; everyone else uses a bearer token.
        if (_http.BaseAddress.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", apiKey);
        }
        else
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string Name => _model;

    /// <summary>Builds a client from TAH_BASE_URL, TAH_API_KEY and TAH_MODEL.</summary>
    public static OpenAiCompatibleClient FromEnvironment(string? modelOverride = null)
    {
        var baseUrl = Environment.GetEnvironmentVariable("TAH_BASE_URL") ?? "https://openrouter.ai/api/v1";
        var apiKey = Environment.GetEnvironmentVariable("TAH_API_KEY")
            ?? throw new InvalidOperationException("Set TAH_API_KEY (and optionally TAH_BASE_URL and TAH_MODEL).");
        var model = modelOverride
            ?? Environment.GetEnvironmentVariable("TAH_MODEL")
            ?? throw new InvalidOperationException("Set TAH_MODEL or pass --model.");
        return new OpenAiCompatibleClient(baseUrl, apiKey, model);
    }

    public async Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(_model, messages, tools).ToJsonString();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("chat/completions", content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Model call failed ({(int)response.StatusCode}): {Truncate(text, 600)}");
        }

        return ParseResponse(text);
    }

    public static JsonObject BuildRequest(string model, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
    {
        var jsonMessages = new JsonArray();
        foreach (var message in messages)
        {
            var item = new JsonObject { ["role"] = message.Role, ["content"] = message.Content };

            if (message.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                    });
                }
                item["tool_calls"] = calls;
            }

            if (message.ToolCallId is not null) item["tool_call_id"] = message.ToolCallId;
            jsonMessages.Add(item);
        }

        var jsonTools = new JsonArray();
        foreach (var tool in tools)
        {
            jsonTools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersSchemaJson),
                },
            });
        }

        var request = new JsonObject { ["model"] = model, ["messages"] = jsonMessages };
        if (jsonTools.Count > 0) request["tools"] = jsonTools;
        return request;
    }

    public static ModelResponse ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");

        string? content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                calls.Add(new ToolCall(
                    call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    function.GetProperty("name").GetString() ?? "",
                    function.GetProperty("arguments").GetString() ?? "{}"));
            }
        }

        return new ModelResponse(content, calls);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
