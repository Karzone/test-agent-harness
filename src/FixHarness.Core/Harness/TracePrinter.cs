using FixHarness.Loop;

namespace FixHarness.Harness;

/// <summary>Prints what the agent actually did: every tool call and its result.</summary>
public static class TracePrinter
{
    public static void Print(LoopResult loop, Action<string> write)
    {
        foreach (var iteration in loop.Trace)
        {
            var context = $"[ctx: {iteration.ContextSize}{(iteration.ContextTrimmed ? ", trimmed" : "")}]";
            if (iteration.Answered)
            {
                write($"[iter {iteration.Index}] answered {context}");
                continue;
            }

            write($"[iter {iteration.Index}] {iteration.ToolEvents.Count} tool call(s) {context}");
            foreach (var toolEvent in iteration.ToolEvents)
            {
                write($"  -> {toolEvent.Tool}({AgentLoop.OneLine(toolEvent.ArgumentsJson, 110)})");
                write($"     {AgentLoop.OneLine(toolEvent.Result, 130)}");
            }
        }

        write($"Stopped by: {loop.StoppedBy} after {loop.Iterations} iteration(s)");
    }
}
