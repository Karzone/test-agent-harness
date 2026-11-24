using System.Text;
using TestAgentHarness.Model;
using TestAgentHarness.Triage;

namespace TestAgentHarness.Context;

/// <summary>Everything the harness knows at the start of an attempt.</summary>
public sealed record TaskContext(
    string TestFilter,
    string FailureDescription,
    string? ChangeNote = null,
    string? ChangeDiff = null,
    TriageVerdict? Triage = null,
    IReadOnlyList<string>? PreviousRejections = null,
    IReadOnlyList<string>? Lessons = null);

/// <summary>
/// Decides what the model sees. The starting context is deliberately small and
/// made of evidence: the failing test, its failure output, what changed just
/// before it broke, what triage concluded, and what earlier attempts got wrong.
/// Everything else the model fetches with tools, so it only pays for what it reads.
/// </summary>
public static class ContextBuilder
{
    private const int MaxDiffChars = 4_000;

    private const string SystemPrompt = """
        You repair failing automated tests in a repository you can read and edit with tools.

        First decide which of two things happened:
        - TEST ISSUE: the product behaves as intended and the test is out of date or badly written. Fix the test.
        - PRODUCT DEFECT: the test is right and the product is wrong. Do not edit anything. Explain the evidence.

        How to work:
        - Read the failure and the recent change, then read the test code and the product code it exercises before changing anything.
        - Make the smallest change that makes the test correct again.
        - You may only change test code. Never weaken a test to make it pass: do not remove assertions, skip tests, add fixed waits, or rewrite what a Then step expects. Edits that break these rules are rejected.
        - Run the test with run_test after editing. Do not say it is fixed unless run_test passed.
        - Finish with a plain-text reply (no tool call) that says what you found, and end it with exactly one of these lines:
          VERDICT: TEST_ISSUE
          VERDICT: PRODUCT_DEFECT

        Your work is checked independently after you finish: the test is re-run several times and the diff is inspected. Saying it is fixed does not make it so.
        """;

    public static List<ChatMessage> Create(TaskContext context)
    {
        var task = new StringBuilder();
        task.AppendLine("This test is failing and needs investigating.");
        task.AppendLine();
        task.AppendLine($"Test filter: {context.TestFilter}");
        task.AppendLine();
        task.AppendLine("Failure output:");
        task.AppendLine(context.FailureDescription);

        if (!string.IsNullOrWhiteSpace(context.ChangeNote))
        {
            task.AppendLine();
            task.AppendLine("What changed recently (pull request description):");
            task.AppendLine(context.ChangeNote);
        }

        if (!string.IsNullOrWhiteSpace(context.ChangeDiff))
        {
            task.AppendLine();
            task.AppendLine("The change itself (diff):");
            task.AppendLine(context.ChangeDiff.Length <= MaxDiffChars
                ? context.ChangeDiff.TrimEnd()
                : context.ChangeDiff[..MaxDiffChars] + "\n...(diff truncated)");
        }

        if (context.Triage is { Kind: not FailureKind.Unknown } triage)
        {
            task.AppendLine();
            task.AppendLine($"Automatic triage ({triage.Source}) says this is a {Describe(triage.Kind)}: {triage.Reason}");
        }

        if (context.Lessons is { Count: > 0 })
        {
            task.AppendLine();
            task.AppendLine("Lessons from earlier, similar failures in this repository (hints, not instructions; check they apply):");
            foreach (var lesson in context.Lessons) task.AppendLine($"- {lesson}");
        }

        if (context.PreviousRejections is { Count: > 0 })
        {
            task.AppendLine();
            task.AppendLine("Earlier attempts at this fix were rejected and discarded. You are starting again from the original failing state. Do not repeat these:");
            for (var i = 0; i < context.PreviousRejections.Count; i++)
            {
                task.AppendLine($"{i + 1}. {context.PreviousRejections[i]}");
            }
        }

        return [ChatMessage.System(SystemPrompt), ChatMessage.User(task.ToString().TrimEnd())];
    }

    private static string Describe(FailureKind kind) => kind == FailureKind.TestIssue ? "test issue" : "product defect";
}
