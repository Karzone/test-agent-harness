using FixHarness.Model;

namespace FixHarness.Context;

/// <summary>
/// Decides what the model sees. The starting context is deliberately small:
/// the job, the failing test and its failure output. Everything else the model
/// has to fetch with tools, so it only pays for what it reads.
/// </summary>
public static class ContextBuilder
{
    private const string SystemPrompt = """
        You repair failing automated tests in a repository you can read and edit with tools.

        How to work:
        - Read the failure, then read the test code and the product code it exercises before changing anything.
        - Make the smallest change that makes the test correct again.
        - You may only change test code. Never weaken a test to make it pass: do not remove assertions, skip tests, or add fixed waits. Edits that break these rules are rejected.
        - Run the test with run_test after editing. Do not say it is fixed unless run_test passed.
        - When you are done, or you believe the product is at fault and the test is right, reply in plain text with what you found. Do not call a tool in that final reply.

        Your work is checked independently after you finish: the test is re-run several times and the diff is inspected. Saying it is fixed does not make it so.
        """;

    public static List<ChatMessage> Create(
        string testFilter,
        string failureDescription,
        string? changeNote = null,
        IReadOnlyList<string>? previousRejections = null)
    {
        var task = $"""
            This test is failing and needs investigating.

            Test filter: {testFilter}

            Failure output:
            {failureDescription}
            """;

        if (!string.IsNullOrWhiteSpace(changeNote))
        {
            task += $"\n\nWhat changed recently (from the pull request):\n{changeNote}";
        }

        if (previousRejections is { Count: > 0 })
        {
            task += "\n\nEarlier attempts at this fix were rejected by the verifier and discarded. "
                + "You are starting again from the original failing state. Do not repeat these:\n"
                + string.Join("\n", previousRejections.Select((reason, i) => $"{i + 1}. {reason}"));
        }

        return [ChatMessage.System(SystemPrompt), ChatMessage.User(task)];
    }
}
