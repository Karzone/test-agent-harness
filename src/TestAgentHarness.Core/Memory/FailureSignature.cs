using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TestAgentHarness.Testing;

namespace TestAgentHarness.Memory;

/// <summary>
/// A stable fingerprint of a failure, so the harness can recognise one it has
/// seen before. Two levels:
///
///   ExactKey: the same test failing in the same way (same selector, same values).
///             Timings, line numbers and ids are ignored. Used to replay a known fix
///             and to apply a person's label.
///   Shape:    the kind of failure with the specifics removed ("timeout waiting for
///             locator *"). Used to find similar past failures for hints.
///
/// Both are plain text processing: no model and no embeddings, so the same
/// failure always gets the same fingerprint.
/// </summary>
public sealed partial record FailureSignature(string ExactKey, string Shape, string Headline)
{
    public static FailureSignature From(string filter, TestRunResult baseline)
    {
        var failed = baseline.Tests.FirstOrDefault(t => t.Outcome != TestOutcome.Passed);
        var raw = failed is null
            ? "build failed: " + baseline.OutputTail
            : failed.ErrorMessage ?? failed.StdOut ?? failed.Outcome.ToString();

        var stable = Stabilise(raw);
        var shape = WhitespaceRegex().Replace(NumberRegex().Replace(QuotedRegex().Replace(stable, "*"), "#"), " ").Trim();
        var headline = raw.Trim().Split('\n')[0].Trim();

        return new FailureSignature(
            Hash(filter + "\n" + stable),
            shape.Length <= 400 ? shape : shape[..400],
            headline.Length <= 160 ? headline : headline[..160]);
    }

    /// <summary>How alike two shapes are, from 0 (nothing shared) to 1 (identical), by shared words.</summary>
    public static double Similarity(string shapeA, string shapeB)
    {
        var a = Tokens(shapeA);
        var b = Tokens(shapeB);
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    /// <summary>Removes the parts of an error that differ from run to run without meaning anything.</summary>
    private static string Stabilise(string text)
    {
        var result = text.Replace("\r\n", "\n").ToLowerInvariant();
        result = DurationRegex().Replace(result, "#ms");
        result = LineNumberRegex().Replace(result, "line #");
        result = GuidOrHexRegex().Replace(result, "#id");
        result = PathRegex().Replace(result, "#path");
        return string.Join('\n', result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(8));
    }

    private static HashSet<string> Tokens(string shape) =>
        TokenRegex().Matches(shape).Select(m => m.Value).ToHashSet();

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    [GeneratedRegex(@"\b\d+(\.\d+)?\s*(ms|s|sec|seconds|milliseconds)\b")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\bline \d+\b|:line \d+|:\d+(:\d+)?\)")]
    private static partial Regex LineNumberRegex();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b[0-9a-f]{12,}\b")]
    private static partial Regex GuidOrHexRegex();

    [GeneratedRegex(@"([a-z]:)?([\\/][\w.\-]+){3,}")]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"[^\"\\n]*\"|'[^'\\n]*'")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[a-z_]{3,}")]
    private static partial Regex TokenRegex();
}
