using FixHarness.Loop;
using FixHarness.Model;

namespace FixHarness.Guardrails;

/// <summary>What a loop guardrail can look at before each model call.</summary>
public sealed record LoopState(int Iterations, IReadOnlyList<ChatMessage> Messages, TimeSpan Elapsed, IReadOnlyList<ToolEvent> ToolEvents);

/// <summary>Returns null to carry on, or the reason to stop.</summary>
public delegate string? LoopGuardrail(LoopState state);

/// <summary>
/// Checks that run before every model call. They are plain code, so the model
/// cannot talk its way past them. They catch structural failure (a run that
/// never ends); they say nothing about whether the answer is right.
/// </summary>
public static class LoopGuardrails
{
    public static LoopGuardrail MaxIterations(int limit) => state =>
        state.Iterations >= limit ? $"Guardrail: reached the iteration limit ({limit})." : null;

    public static LoopGuardrail TimeBudget(TimeSpan limit) => state =>
        state.Elapsed >= limit ? $"Guardrail: ran out of time ({limit.TotalMinutes:0.#} minute budget)." : null;

    /// <summary>The same tool call with the same result several times in a row means the model is stuck.</summary>
    public static LoopGuardrail NotStuck(int repeats = 3) => state =>
    {
        if (state.ToolEvents.Count < repeats) return null;
        var recent = state.ToolEvents.Skip(state.ToolEvents.Count - repeats).ToList();
        return recent.All(e => e == recent[0])
            ? $"Guardrail: the same {recent[0].Tool} call returned the same result {repeats} times in a row."
            : null;
    };

    public static LoopGuardrail Combine(params LoopGuardrail[] guardrails) => state =>
    {
        foreach (var guardrail in guardrails)
        {
            if (guardrail(state) is { } reason) return reason;
        }
        return null;
    };

    public static LoopGuardrail Default { get; } = Combine(
        MaxIterations(20),
        TimeBudget(TimeSpan.FromMinutes(15)),
        NotStuck());
}
