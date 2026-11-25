using System.Text.RegularExpressions;
using TestAgentHarness.Testing;

namespace TestAgentHarness.Triage;

public enum FailureKind
{
    /// <summary>The test is out of date or badly written; the product is fine. Worth auto-fixing.</summary>
    TestIssue,
    /// <summary>The test is right and the product is wrong. Must be reported, never "fixed".</summary>
    ProductDefect,
    /// <summary>
    /// Neither: the system under test could not be reached (service down, DNS, gateway).
    /// Nothing in the tests or the product code will fix that, so nothing is changed.
    /// </summary>
    Environment,
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
    string? ChangeNote,
    string? ChangeDiff = null)
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
        EnvironmentUnavailable,
        BrowserScriptError,
        ServerError,
        OnlyTestCodeChanged,
        NotFoundAfterRouteChange,
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

    /// <summary>
    /// The system under test could not be reached at all: connection refused, DNS
    /// failure, a gateway error. Sending this to a model invites it to "fix" a
    /// test that is not broken.
    /// </summary>
    public static TriageVerdict? EnvironmentUnavailable(FailureEvidence evidence)
    {
        var match = EnvironmentRegex().Match(evidence.FailureText);
        return match.Success
            ? new(FailureKind.Environment, nameof(EnvironmentUnavailable), $"The system under test could not be reached: {match.Value.Trim()}")
            : null;
    }

    /// <summary>
    /// A request came back 404 and the change moved or renamed a route in product
    /// code: the test is calling the old address.
    /// </summary>
    public static TriageVerdict? NotFoundAfterRouteChange(FailureEvidence evidence)
    {
        if (!NotFoundRegex().IsMatch(evidence.FailureText) || evidence.ChangeDiff is null) return null;

        var routeLine = evidence.ChangeDiff.Split('\n')
            .FirstOrDefault(line => (line.StartsWith('+') || line.StartsWith('-')) && !line.StartsWith("+++") && !line.StartsWith("---") && RouteRegex().IsMatch(line));
        return routeLine is not null && evidence.ChangedProductPaths.Any()
            ? new(FailureKind.TestIssue, nameof(NotFoundAfterRouteChange), $"A request returned 404 and the change altered a route ({routeLine.TrimStart('+', '-').Trim()}), so the test is calling an address that moved.")
            : null;
    }

    /// <summary>The page threw an uncaught JavaScript error while the test ran.</summary>
    public static TriageVerdict? BrowserScriptError(FailureEvidence evidence)
    {
        var match = PageErrorRegex().Match(evidence.FailureText);
        return match.Success
            ? new(FailureKind.ProductDefect, nameof(BrowserScriptError), $"The application threw a script error during the test: {match.Value.Trim()}")
            : null;
    }

    /// <summary>The application itself failed with a 500. (502, 503 and 504 are environment problems, handled earlier.)</summary>
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
        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "PRODUCT_DEFECT" => FailureKind.ProductDefect,
            "ENVIRONMENT" => FailureKind.Environment,
            _ => FailureKind.TestIssue,
        };
    }

    [GeneratedRegex(@"(pageerror|Uncaught)\b[^\n]*|\b(ReferenceError|TypeError|SyntaxError|RangeError):[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex PageErrorRegex();

    [GeneratedRegex(@"\b(HTTP|status(\s*code)?)\s*[:=]?\s*50[01]\b[^\n]*|\b50[01]\s+\(?(Internal Server Error|Not Implemented)\)?[^\n]*|But was:\s*InternalServerError[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ServerErrorRegex();

    [GeneratedRegex(@"\b(HTTP|status(\s*code)?)\s*[:=]?\s*50[234]\b[^\n]*|Connection refused[^\n]*|actively refused[^\n]*|No such host is known[^\n]*|Name or service not known[^\n]*|nodename nor servname[^\n]*|ERR_CONNECTION_REFUSED[^\n]*|ERR_NAME_NOT_RESOLVED[^\n]*|The request was canceled due to the configured HttpClient\.Timeout[^\n]*|\b50[234]\s+\(?(Bad Gateway|Service Unavailable|Gateway Timeout)\)?[^\n]*|But was:\s*(BadGateway|ServiceUnavailable|GatewayTimeout)[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentRegex();

    [GeneratedRegex(@"\b404\s+\(?Not Found\)?|status\s*code\s*[:=]?\s*404|But was:\s*NotFound", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundRegex();

    [GeneratedRegex(@"\bMap(Get|Post|Put|Delete|Patch|Group)\s*\(|\[(Http(Get|Post|Put|Delete|Patch)|Route)\b|@(Get|Post|Put|Delete|Request)Mapping|\b(router|app)\.(get|post|put|delete|patch)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex RouteRegex();

    [GeneratedRegex(@"no\s+(behaviou?r(al)?|functional)\s+change|behaviou?r\s+(is\s+)?unchanged|pure\s+refactor", RegexOptions.IgnoreCase)]
    private static partial Regex NoBehaviourChangeRegex();

    [GeneratedRegex(@"Expected\s*:[^\n]*\n\s*But was\s*:|expected to have text|Expected string|Assert\.\w+\(\) Failure", RegexOptions.IgnoreCase)]
    private static partial Regex ValueMismatchRegex();

    [GeneratedRegex(@"Timeout \d+ms exceeded[\s\S]{0,300}waiting for (Locator|GetBy)|waiting for (Locator|GetBy)[\s\S]{0,300}Timeout \d+ms exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex LocatorTimeoutRegex();

    [GeneratedRegex(@"\.(html?|cshtml|razor|jsx|tsx|vue|svelte)$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupFileRegex();

    [GeneratedRegex(@"VERDICT\s*:\s*(TEST_ISSUE|PRODUCT_DEFECT|ENVIRONMENT)", RegexOptions.IgnoreCase)]
    private static partial Regex ModelVerdictRegex();
}
