using FixHarness.Model;
using FixHarness.Tools;

namespace FixHarness.Loop;

/// <summary>One tool call and its result, kept for the trace.</summary>
public sealed record ToolEvent(string Tool, string ArgumentsJson, string Result);

/// <summary>One trip round the loop: the model either called tools or gave its answer.</summary>
public sealed record LoopIteration(int Index, bool Answered, IReadOnlyList<ToolEvent> ToolEvents, int ContextSize);

public enum StoppedBy
{
    Model,
}

public sealed record LoopResult(string Answer, IReadOnlyList<LoopIteration> Trace, StoppedBy StoppedBy)
{
    public int Iterations => Trace.Count;
    public IEnumerable<ToolEvent> ToolEvents => Trace.SelectMany(i => i.ToolEvents);
}

/// <summary>
/// The agent loop: ask the model, run the tools it asks for, give it the
/// results, repeat until it answers in plain text.
///
/// This is the whole "agent". Note what it does not have: nothing stops it
/// looping forever, nothing limits what it edits, and nothing checks whether
/// its final answer is true.
/// </summary>
public static class AgentLoop
{
    private const int MaxToolResultChars = 6_000;

    public static async Task<LoopResult> RunAsync(
        IModelClient model,
        List<ChatMessage> messages,
        ToolRegistry tools,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var trace = new List<LoopIteration>();

        while (true)
        {
            var index = trace.Count + 1;
            var contextSize = messages.Count;

            var response = await model.CompleteAsync(messages, tools.Definitions, cancellationToken);
            messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls));

            // No tool calls means the model is giving its final answer.
            if (response.ToolCalls.Count == 0)
            {
                log?.Invoke($"[iter {index}] answered");
                trace.Add(new LoopIteration(index, Answered: true, [], contextSize));
                return new LoopResult(response.Content ?? "(no answer)", trace, StoppedBy.Model);
            }

            var events = new List<ToolEvent>();
            foreach (var call in response.ToolCalls)
            {
                var result = Truncate(await tools.ExecuteAsync(call, cancellationToken));
                log?.Invoke($"[iter {index}] {call.Name}({OneLine(call.ArgumentsJson, 100)}) -> {OneLine(result, 110)}");
                events.Add(new ToolEvent(call.Name, call.ArgumentsJson, result));
                messages.Add(ChatMessage.ToolResult(call.Id, result));
            }

            trace.Add(new LoopIteration(index, Answered: false, events, contextSize));
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxToolResultChars ? text : text[..MaxToolResultChars] + "\n...(truncated)";

    public static string OneLine(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..max] + "...";
    }
}
