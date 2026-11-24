using System.Text.Json;

namespace FixHarness.Model;

/// <summary>
/// A stand-in "model" that replays a fixed script of tool calls. It needs no API
/// key, so it is used by the self-tests and by the offline demo to show what the
/// harness does with a badly behaved model.
///
/// Script format (JSON): { "attempts": [ [ turn, turn, ... ], [ ... ] ] }
/// where a turn is { "say": "text", "calls": [ { "tool": "name", "args": { ... } } ] }.
/// A turn with no calls is a final answer. Each new conversation plays the next attempt.
/// </summary>
public sealed class ScriptedModelClient : IModelClient
{
    public sealed record Turn(string? Say, IReadOnlyList<(string Tool, string ArgsJson)> Calls);

    private readonly IReadOnlyList<IReadOnlyList<Turn>> _attempts;
    private int _attempt = -1;
    private int _turn;
    private int _callId;

    public ScriptedModelClient(IReadOnlyList<IReadOnlyList<Turn>> attempts) => _attempts = attempts;

    public string Name => "scripted";

    public static ScriptedModelClient FromFile(string path) => FromJson(File.ReadAllText(path));

    public static ScriptedModelClient FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attempts = new List<IReadOnlyList<Turn>>();

        foreach (var attempt in document.RootElement.GetProperty("attempts").EnumerateArray())
        {
            var turns = new List<Turn>();
            foreach (var turn in attempt.EnumerateArray())
            {
                var say = turn.TryGetProperty("say", out var s) ? s.GetString() : null;
                var calls = new List<(string, string)>();
                if (turn.TryGetProperty("calls", out var callList))
                {
                    foreach (var call in callList.EnumerateArray())
                    {
                        var args = call.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                        calls.Add((call.GetProperty("tool").GetString() ?? "", args));
                    }
                }
                turns.Add(new Turn(say, calls));
            }
            attempts.Add(turns);
        }

        return new ScriptedModelClient(attempts);
    }

    public Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        // A conversation with no assistant message yet is the start of a new attempt.
        if (!messages.Any(m => m.Role == "assistant"))
        {
            _attempt++;
            _turn = 0;
        }

        var script = _attempt < _attempts.Count ? _attempts[_attempt] : [];
        if (_turn >= script.Count)
        {
            return Task.FromResult(new ModelResponse("(script finished)", []));
        }

        var turn = script[_turn++];
        var calls = turn.Calls
            .Select(call => new ToolCall($"call_{++_callId}", call.Tool, call.ArgsJson))
            .ToList();
        return Task.FromResult(new ModelResponse(turn.Say, calls));
    }
}
