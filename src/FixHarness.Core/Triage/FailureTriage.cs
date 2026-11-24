using System.Text.RegularExpressions;
using FixHarness.Testing;

namespace FixHarness.Triage;

public enum FailureKind
{
    /// <summary>The test is out of date or badly written; the product is fine. Worth auto-fixing.</summary>
    TestIssue,
    /// <summary>The test is right and the product is wrong. Must be reported, never "fixed".</summary>
    ProductDefect,
    /// <summary>The rules could not tell. The model investigates.</summary>
    Unknown,
}

/// <param name="Source">Which rule decided, or "model" / "human label". Shown in every report.</param>
public sealed record TriageVerdict(FailureKind Kind, string Source, string Reason)
{
    public static TriageVerdict Unknown { get; } = new(FailureKind.Unknown, "none", "No triage rule matched; the model will investigate.");
}

/// <summary>Everything a rule may look at. All of it is gathered by the harness, none of it by the model.</summary>
public sealed record FailureEvidence(
    TestRunResult Baseline,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> TestRoots,
    string? ChangeNote)
{
    /// <summary>Error message, stack and printed output of every test that did not pass.</summary>
    public string FailureText { get; } = string.Join("\n", Baseline.Tests
        .Where(t => t.Outcome != TestOutcome.Passed)
        .Select(t => $"{t.ErrorMessage}\n{t.StackTrace}\n{t.StdOut}"));

    public bool IsTestPath(string path) =>
        TestRoots.Any(root => path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> ChangedProductPaths => ChangedPaths.Where(p => !IsTestPath(p));
}

/// <summary>A rule returns a verdict, or null when it has nothing to say.</summary>
public delegate TriageVerdict? TriageRule(FailureEvidence evidence);

/// <summary>
/// Classifies a failure with plain code before any model is involved.
///
/// Why rules first: they are instant, free, repeatable and explainable, and a
/// wrong one can be fixed by editing one function. The model is only asked
/// about the failures the rules cannot settle. Rules run in order; the first
/// verdict wins, so the most certain evidence goes first.
/// </summary>
public static partial class FailureTriage
{
    public static IReadOnlyList<TriageRule> DefaultRules { get; } =
    [
        MissingStepBinding,
        BrowserScriptError,
        ServerError,
        OnlyTestCodeChanged,
        BehaviourChangedByRefactor,
        LocatorTimeoutAfterMarkupChange,
    ];

    public static TriageVerdict Classify(FailureEvidence evidence, IEnumerable<TriageRule>? rules = null)
    {
        foreach (var rule in rules ?? DefaultRules)
        {
            if (rule(evidence) is { } verdict) return verdict;
        }
        return TriageVerdict.Unknown;
    }

    /// <summary>A step in a feature file has no binding. Only test code can cause or cure that.</summary>
    public static TriageVerdict? MissingStepBinding(FailureEvidence evidence) =>
        evidence.FailureText.Contains("No matching step definition", StringComparison.OrdinalIgnoreCase)
            ? new(FailureKind.TestIssue, nameof(MissingStepBinding), "A step in the feature file has no matching step definition.")
            : null;

    /// <summary>The page threw an uncaught JavaScript error while the test ran.</summary>
    public static TriageVerdict? BrowserScriptError(FailureEvidence evidence)
    {
        var match = PageErrorRegex().Match(evidence.FailureText);
        return match.Success
            ? new(FailureKind.ProductDefect, nameof(BrowserScriptError), $"The application threw a script error during the test: {match.Value.Trim()}")
            : null;
    }

    /// <summary>The application answered with a 5xx.</summary>
    public static TriageVerdict? ServerError(FailureEvidence evidence)
    {
        var match = ServerErrorRegex().Match(evidence.FailureText);
        return match.Success
            ? new(FailureKind.ProductDefect, nameof(ServerError), $"The application returned a server error: {match.Value.Trim()}")
            : null;
    }

    /// <summary>If only test code changed since the test last passed, the product cannot be to blame.</summary>
    public static TriageVerdict? OnlyTestCodeChanged(FailureEvidence evidence) =>
        evidence.ChangedPaths.Count > 0 && !evidence.ChangedProductPaths.Any()
            ? new(FailureKind.TestIssue, nameof(OnlyTestCodeChanged), "Only test code changed since the test last passed: " + string.Join(", ", evidence.ChangedPaths))
            : null;

    /// <summary>
    /// The change was described as a refactor with no behaviour change, product
    /// code changed, and an assertion now sees a different value. The behaviour
    /// did change, so the refactor is wrong.
    /// </summary>
    public static TriageVerdict? BehaviourChangedByRefactor(FailureEvidence evidence)
    {
        if (evidence.ChangeNote is null || !NoBehaviourChangeRegex().IsMatch(evidence.ChangeNote)) return null;
        if (!evidence.ChangedProductPaths.Any()) return null;
        if (!ValueMismatchRegex().IsMatch(evidence.FailureText)) return null;

        return new(FailureKind.ProductDefect, nameof(BehaviourChangedByRefactor),
            "The change was described as having no behaviour change, but an assertion now sees a different value after product code changed: "
            + string.Join(", ", evidence.ChangedProductPaths));
    }

    /// <summary>An element could not be found, and the change touched markup: most likely a stale locator.</summary>
    public static TriageVerdict? LocatorTimeoutAfterMarkupChange(FailureEvidence evidence)
    {
        if (!LocatorTimeoutRegex().IsMatch(evidence.FailureText)) return null;
        var markup = evidence.ChangedProductPaths.Where(p => MarkupFileRegex().IsMatch(p)).ToList();
        return markup.Count > 0
            ? new(FailureKind.TestIssue, nameof(LocatorTimeoutAfterMarkupChange), "An element was not found and the change touched markup (" + string.Join(", ", markup) + "), which points to a stale locator.")
            : null;
    }

    /// <summary>
    /// Reads the verdict line the model is asked to end its answer with. Returns
    /// Unknown when there is none; a model's silence is never treated as a verdict.
    /// </summary>
    public static FailureKind ParseModelVerdict(string answer)
    {
        var match = ModelVerdictRegex().Matches(answer).LastOrDefault();
        if (match is null) return FailureKind.Unknown;
        return match.Groups[1].Value.ToUpperInvariant() == "PRODUCT_DEFECT" ? FailureKind.ProductDefect : FailureKind.TestIssue;
    }

    [GeneratedRegex(@"(pageerror|Uncaught)\b[^\n]*|\b(ReferenceError|TypeError|SyntaxError|RangeError):[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex PageErrorRegex();

    [GeneratedRegex(@"\b(HTTP|status(\s*code)?)\s*[:=]?\s*5\d\d\b[^\n]*|\b5\d\d\s+\(?(Internal Server Error|Bad Gateway|Service Unavailable|Gateway Timeout)\)?[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ServerErrorRegex();

    [GeneratedRegex(@"no\s+(behaviou?r(al)?|functional)\s+change|behaviou?r\s+(is\s+)?unchanged|pure\s+refactor", RegexOptions.IgnoreCase)]
    private static partial Regex NoBehaviourChangeRegex();

    [GeneratedRegex(@"Expected\s*:[^\n]*\n\s*But was\s*:|expected to have text|Expected string|Assert\.\w+\(\) Failure", RegexOptions.IgnoreCase)]
    private static partial Regex ValueMismatchRegex();

    [GeneratedRegex(@"Timeout \d+ms exceeded[\s\S]{0,300}waiting for (Locator|GetBy)|waiting for (Locator|GetBy)[\s\S]{0,300}Timeout \d+ms exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex LocatorTimeoutRegex();

    [GeneratedRegex(@"\.(html?|cshtml|razor|jsx|tsx|vue|svelte)$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupFileRegex();

    [GeneratedRegex(@"VERDICT\s*:\s*(TEST_ISSUE|PRODUCT_DEFECT)", RegexOptions.IgnoreCase)]
    private static partial Regex ModelVerdictRegex();
}
