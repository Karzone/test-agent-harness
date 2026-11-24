using System.Text.RegularExpressions;

namespace FixHarness.Guardrails;

/// <summary>A file's content before the agent touched it and as it would be after the edit.</summary>
public sealed record FileChange(string Original, string Current);

public sealed record PatchPolicyOptions(IReadOnlyList<string> AllowedRoots, int MaxFilesChanged = 3, int MaxChangedLines = 40);

/// <summary>
/// The rules a fix must obey. A test can always be made green by cheating:
/// edit the product, delete the assertion, skip the test, add a sleep. Each
/// rule here closes one of those routes, in plain code.
///
/// The same policy is applied twice: to every edit as the model makes it (so it
/// gets the rejection as feedback and can try something else), and to the final
/// diff by the verifier (so nothing slips through another way).
/// </summary>
public sealed partial class PatchPolicy(PatchPolicyOptions options)
{
    private static readonly Regex Assertions = AssertionRegex();
    private static readonly Regex SkipMarkers = SkipRegex();
    private static readonly Regex BlanketWaits = WaitRegex();
    private static readonly Regex SwallowedExceptions = EmptyCatchRegex();
    private static readonly Regex GherkinSteps = GherkinStepRegex();
    private static readonly Regex GherkinThen = GherkinThenRegex();
    private static readonly Regex GherkinScenarios = GherkinScenarioRegex();

    public PatchPolicyOptions Options => options;

    /// <summary>Returns every rule the change set breaks. Empty means allowed.</summary>
    public IReadOnlyList<string> Check(IReadOnlyDictionary<string, FileChange> changes)
    {
        var violations = new List<string>();

        foreach (var (path, change) in changes)
        {
            if (!IsUnderAllowedRoot(path))
            {
                violations.Add($"{path} is not test code. Only files under [{string.Join(", ", options.AllowedRoots)}] may be changed; product code is off limits.");
                continue;
            }

            var before = StripComments(path, change.Original);
            var after = StripComments(path, change.Current);

            if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                Decreased(violations, path, GherkinScenarios, before, after, "removes a scenario");
                Decreased(violations, path, GherkinSteps, before, after, "removes a step");
                Decreased(violations, path, GherkinThen, before, after, "removes a Then step (an assertion)");
            }
            else
            {
                Decreased(violations, path, Assertions, before, after, "removes an assertion");
            }

            Increased(violations, path, SkipMarkers, before, after, "skips or ignores a test");
            Increased(violations, path, BlanketWaits, before, after, "adds a fixed wait (sleep or delay); use a retrying assertion instead");
            Increased(violations, path, SwallowedExceptions, before, after, "adds an empty catch block");
        }

        if (changes.Count > options.MaxFilesChanged)
        {
            violations.Add($"The fix touches {changes.Count} files; the limit is {options.MaxFilesChanged}.");
        }

        var changedLines = changes.Values.Sum(c => CountChangedLines(c.Original, c.Current));
        if (changedLines > options.MaxChangedLines)
        {
            violations.Add($"The fix changes about {changedLines} lines; the limit is {options.MaxChangedLines}.");
        }

        return violations;
    }

    public bool IsUnderAllowedRoot(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return options.AllowedRoots
            .Select(root => root.Replace('\\', '/').TrimEnd('/') + "/")
            .Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lines removed plus lines added, ignoring order. Rough, but stable and cheap.</summary>
    public static int CountChangedLines(string original, string current)
    {
        var remaining = new Dictionary<string, int>();
        foreach (var line in Lines(original)) remaining[line] = remaining.GetValueOrDefault(line) + 1;

        var added = 0;
        foreach (var line in Lines(current))
        {
            if (remaining.GetValueOrDefault(line) > 0) remaining[line]--;
            else added++;
        }

        return added + remaining.Values.Sum();
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());

    private static void Decreased(List<string> violations, string path, Regex pattern, string before, string after, string what)
    {
        if (pattern.Matches(after).Count < pattern.Matches(before).Count) violations.Add($"{path}: the change {what}.");
    }

    private static void Increased(List<string> violations, string path, Regex pattern, string before, string after, string what)
    {
        if (pattern.Matches(after).Count > pattern.Matches(before).Count) violations.Add($"{path}: the change {what}.");
    }

    /// <summary>Commenting an assertion out must count as removing it.</summary>
    private static string StripComments(string path, string text)
    {
        var marker = path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) ? "#" : "//";
        var lines = Lines(text).Select(line =>
        {
            if (marker == "#") return line.TrimStart().StartsWith('#') ? "" : line;
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? line[..index] : line;
        });
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\bAssert\s*\.\s*\w+|\bExpect\s*\(|\.Should\w*\s*\(")]
    private static partial Regex AssertionRegex();

    [GeneratedRegex(@"\[\s*(Ignore|Explicit|Skip)\b|\bAssert\s*\.\s*(Ignore|Inconclusive|Pass)\b|@(ignore|skip)\b|\bSkip\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SkipRegex();

    [GeneratedRegex(@"\bThread\s*\.\s*Sleep\b|\bTask\s*\.\s*Delay\b|\bWaitForTimeoutAsync\b")]
    private static partial Regex WaitRegex();

    [GeneratedRegex(@"\bcatch\s*(\([^)]*\))?\s*\{\s*\}")]
    private static partial Regex EmptyCatchRegex();

    [GeneratedRegex(@"^\s*(Given|When|Then|And|But)\b", RegexOptions.Multiline)]
    private static partial Regex GherkinStepRegex();

    [GeneratedRegex(@"^\s*Then\b", RegexOptions.Multiline)]
    private static partial Regex GherkinThenRegex();

    [GeneratedRegex(@"^\s*(Scenario|Scenario Outline|Example)\s*:", RegexOptions.Multiline)]
    private static partial Regex GherkinScenarioRegex();
}
