using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestAgentHarness.Loop;

namespace TestAgentHarness.Harness;

/// <summary>Turns a result into things people and pipelines can use: console text, markdown, JSON.</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(object result) => JsonSerializer.Serialize(result, JsonOptions);

    public static void Print(FixResult result, Action<string> write)
    {
        foreach (var attempt in result.Attempts)
        {
            write($"\n--- Attempt {attempt.Number}: agent trace ---\n");
            TracePrinter.Print(attempt.Loop, write);
            write($"Agent said: {AgentLoop.OneLine(attempt.Loop.Answer, 200)}");
            write($"Verify:     {(attempt.Verification.Passed ? "PASS" : "FAIL")} - {attempt.Verification.Reason}");
        }

        write("\n--- Result ---\n");
        write($"Outcome:     {result.Outcome}");
        write($"Triage:      {result.Triage.Kind} ({result.Triage.Source})");
        write($"Attempts:    {result.Attempts.Count}");
        write($"Model calls: {result.ModelCalls}");
        write($"Time:        {result.Duration.TotalSeconds:0.0} s");
        if (result.ReplayedFromMemory) write("Memory:      replayed a remembered fix");
        else if (result.LessonsUsed.Count > 0) write($"Memory:      {result.LessonsUsed.Count} lesson(s) from similar failures were given to the model");
        write(result.Summary);
    }

    /// <summary>A short report suitable for a pull-request comment or a defect ticket.</summary>
    public static string ToMarkdown(FixResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(result.Outcome switch
        {
            FixOutcome.ProductDefect => "## test-agent-harness: product defect (test not changed)",
            FixOutcome.EnvironmentIssue => "## test-agent-harness: environment problem (nothing changed)",
            _ => $"## test-agent-harness: {result.Outcome}",
        });
        text.AppendLine();
        text.AppendLine($"- **Test:** `{result.Filter}`");
        text.AppendLine($"- **Triage:** {result.Triage.Kind}, decided by {result.Triage.Source}");
        text.AppendLine($"- **Model:** {result.Model} ({result.ModelCalls} call(s), {result.Attempts.Count} attempt(s))");
        if (result.ReplayedFromMemory) text.AppendLine("- **Memory:** this exact failure was fixed before; that fix was replayed and verified again");
        foreach (var lesson in result.LessonsUsed) text.AppendLine($"- **Lesson used:** {lesson}");
        text.AppendLine();
        text.AppendLine(result.Summary);

        if (result.Outcome == FixOutcome.ProductDefect)
        {
            text.AppendLine();
            text.AppendLine("### Why this is a product defect");
            text.AppendLine();
            text.AppendLine(result.Triage.Reason);
            if (result.Triage.Source == "model")
            {
                text.AppendLine();
                text.AppendLine("> This verdict is a model's judgement, not a rule. Confirm it before raising the defect. If it is wrong, record that with `tah feedback --label test-issue` so the same failure is handled correctly next time.");
            }
        }

        if (result.Outcome == FixOutcome.EnvironmentIssue)
        {
            text.AppendLine();
            text.AppendLine("### Why this is an environment problem");
            text.AppendLine();
            text.AppendLine(result.Triage.Reason);
            text.AppendLine();
            text.AppendLine("Neither the test nor the product was changed. Re-run once the environment is healthy.");
        }

        text.AppendLine();
        text.AppendLine("### Failure");
        text.AppendLine();
        text.AppendLine("```");
        text.AppendLine(result.Baseline.Describe(1500));
        text.AppendLine("```");

        foreach (var attempt in result.Attempts)
        {
            text.AppendLine();
            text.AppendLine($"### Attempt {attempt.Number}: {(attempt.Verification.Passed ? "verified" : "rejected")}");
            text.AppendLine();
            text.AppendLine(attempt.Verification.Reason);
        }

        if (result.Patch is { Length: > 0 })
        {
            text.AppendLine();
            text.AppendLine("### Verified patch");
            text.AppendLine();
            text.AppendLine("```diff");
            text.AppendLine(result.Patch.TrimEnd());
            text.AppendLine("```");
        }

        return text.ToString();
    }
}
