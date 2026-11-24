using System.Diagnostics;
using TestAgentHarness.Context;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Model;
using TestAgentHarness.Tools;

namespace TestAgentHarness.Loop;

/// <summary>One tool call and its result, kept for the trace.</summary>
public sealed record ToolEvent(string Tool, string ArgumentsJson, string Result);

/// <summary>One trip round the loop: the model either called tools or gave its answer.</summary>
public sealed record LoopIteration(int Index, bool Answered, IReadOnlyList<ToolEvent> ToolEvents, int ContextSize, bool ContextTrimmed);

public enum StoppedBy
{
    Model,
    Guardrail,
}

public sealed record LoopResult(string Answer, IReadOnlyList<LoopIteration> Trace, StoppedBy StoppedBy)
{
    public int Iterations => Trace.Count;
    public IEnumerable<ToolEvent> ToolEvents => Trace.SelectMany(i => i.ToolEvents);
}

/// <summary>
/// The agent loop: ask the model, run the tools it asks for, give it the
/// results, repeat until it answers in plain text or a guardrail stops it.
///
/// Before every model call the loop trims the context and checks the
/// guardrails. It still does not check whether the final answer is true;
/// that is the verifier's job.
/// </summary>
public static class AgentLoop
{
    private const int MaxContextMessages = 30;
    private const int MaxToolResultChars = 6_000;

    public static async Task<LoopResult> RunAsync(
        IModelClient model,
        List<ChatMessage> messages,
        ToolRegistry tools,
        LoopGuardrail guardrail,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var trace = new List<LoopIteration>();
        var allEvents = new List<ToolEvent>();
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var index = trace.Count + 1;

            var trimmed = ContextTrimmer.Trim(messages, MaxContextMessages);

            if (guardrail(new LoopState(trace.Count, messages, clock.Elapsed, allEvents)) is { } reason)
            {
                log?.Invoke($"[iter {index}] stopped: {reason}");
                return new LoopResult(reason, trace, StoppedBy.Guardrail);
            }

            var contextSize = messages.Count;
            var response = await model.CompleteAsync(messages, tools.Definitions, cancellationToken);
            messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls));

            // No tool calls means the model is giving its final answer.
            if (response.ToolCalls.Count == 0)
            {
                log?.Invoke($"[iter {index}] answered");
                trace.Add(new LoopIteration(index, Answered: true, [], contextSize, trimmed));
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

            allEvents.AddRange(events);
            trace.Add(new LoopIteration(index, Answered: false, events, contextSize, trimmed));
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
