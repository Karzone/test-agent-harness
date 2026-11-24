using System.Text;

namespace FixHarness.Testing;

public enum TestOutcome
{
    Passed,
    Failed,
    /// <summary>Skipped, inconclusive, not executed. Never counted as a pass.</summary>
    NotRun,
}

/// <param name="StdOut">What the test printed. Often holds the best clue (browser console errors, missing step bindings).</param>
public sealed record TestCaseResult(
    string Name,
    TestOutcome Outcome,
    string? ErrorMessage = null,
    string? StackTrace = null,
    string? StdOut = null);

/// <summary>The result of running a set of tests once.</summary>
public sealed record TestRunResult(bool BuildSucceeded, IReadOnlyList<TestCaseResult> Tests, string OutputTail)
{
    public bool NoTestsMatched => BuildSucceeded && Tests.Count == 0;

    /// <summary>
    /// True only when the code built, at least one test ran, and every matched
    /// test passed. Zero tests and skipped tests are not a pass: that is the
    /// easiest way for a "fix" to look green without fixing anything.
    /// </summary>
    public bool AllPassed => BuildSucceeded && Tests.Count > 0 && Tests.All(t => t.Outcome == TestOutcome.Passed);

    /// <summary>
    /// The build never got as far as compiling: packages could not be restored or
    /// the SDK is missing. A compile error (CS....) is different: that can be a
    /// genuine test issue, so it is not treated as an environment failure.
    /// </summary>
    public bool IsEnvironmentFailure =>
        !BuildSucceeded
        && System.Text.RegularExpressions.Regex.IsMatch(OutputTail, @"error NU\d+|error NETSDK\d+|Unable to load the service index|MSB1009|Timed out after")
        && !System.Text.RegularExpressions.Regex.IsMatch(OutputTail, @"error CS\d+");

    public IEnumerable<string> NotPassedNames => Tests.Where(t => t.Outcome != TestOutcome.Passed).Select(t => t.Name);

    /// <summary>A compact description for the model and for the trace.</summary>
    public string Describe(int maxChars = 3000)
    {
        var text = new StringBuilder();

        if (!BuildSucceeded)
        {
            text.AppendLine("BUILD FAILED. The tests did not run. Output:");
            text.AppendLine(OutputTail);
        }
        else if (Tests.Count == 0)
        {
            text.AppendLine("NO TESTS MATCHED the filter. This is not a pass.");
        }
        else
        {
            var passed = Tests.Count(t => t.Outcome == TestOutcome.Passed);
            text.AppendLine($"{(AllPassed ? "PASSED" : "FAILED")}: {passed} of {Tests.Count} test(s) passed.");
            foreach (var test in Tests.Where(t => t.Outcome != TestOutcome.Passed))
            {
                text.AppendLine($"- {test.Name}: {test.Outcome}");
                if (!string.IsNullOrWhiteSpace(test.ErrorMessage)) text.AppendLine($"  Error: {test.ErrorMessage.Trim()}");
                if (!string.IsNullOrWhiteSpace(test.StackTrace)) text.AppendLine($"  Stack: {test.StackTrace.Trim()}");
                if (!string.IsNullOrWhiteSpace(test.StdOut)) text.AppendLine($"  Output: {test.StdOut.Trim()}");
            }
        }

        var result = text.ToString().TrimEnd();
        return result.Length <= maxChars ? result : result[..maxChars] + "\n...(truncated)";
    }
}

/// <summary>Runs the tests matching a filter inside a working directory.</summary>
public interface ITestRunner
{
    Task<TestRunResult> RunAsync(string workingDirectory, string filter, CancellationToken cancellationToken = default);
}
