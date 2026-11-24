using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FixHarness.Harness;

/// <summary>Turns a result into things people and pipelines can use: console text, markdown, JSON.</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(FixResult result) => JsonSerializer.Serialize(result, JsonOptions);

    public static void Print(FixResult result, Action<string> write)
    {
        foreach (var attempt in result.Attempts)
        {
            write($"\n--- Attempt {attempt.Number}: agent trace ---\n");
            TracePrinter.Print(attempt.Loop, write);
            write($"Agent said: {Loop.AgentLoop.OneLine(attempt.Loop.Answer, 200)}");
            write($"Verify:     {(attempt.Verification.Passed ? "PASS" : "FAIL")} - {attempt.Verification.Reason}");
        }

        write("\n--- Result ---\n");
        write($"Outcome:  {result.Outcome}");
        write($"Attempts: {result.Attempts.Count}");
        write(result.Summary);
    }

    /// <summary>A short report suitable for a pull-request comment.</summary>
    public static string ToMarkdown(FixResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"## FixHarness: {result.Outcome}");
        text.AppendLine();
        text.AppendLine($"- **Test:** `{result.Filter}`");
        text.AppendLine($"- **Model:** {result.Model}");
        text.AppendLine($"- **Attempts:** {result.Attempts.Count}");
        text.AppendLine();
        text.AppendLine(result.Summary);
        text.AppendLine();
        text.AppendLine("### Original failure");
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
