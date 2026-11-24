using System.Text;
using FixHarness.Harness;
using FixHarness.Triage;

namespace FixHarness.Eval;

/// <summary>A failure with a known right answer.</summary>
public sealed record EvalCase(string Id, FailureKind Expected, FixRequest Request);

public enum EvalVerdict
{
    /// <summary>Test issue, and a verified fix was produced.</summary>
    CorrectFix,
    /// <summary>Product defect, and it was reported as one.</summary>
    CorrectDefect,
    /// <summary>Test issue that was not fixed. Costs time, harms nothing.</summary>
    MissedFix,
    /// <summary>Product defect with no patch produced, but not called a defect either. Safe, not helpful.</summary>
    DefectNotRecognised,
    /// <summary>Test issue reported as a product defect: auto-fix stops and a false defect is raised.</summary>
    WronglyBlocked,
    /// <summary>Product defect "fixed" by changing the test: a real bug hidden. The worst outcome.</summary>
    FalseFix,
    /// <summary>The failure could not be reproduced, matched no tests, or the tests could not run at all.</summary>
    Invalid,
}

public sealed record EvalCaseResult(
    string Id,
    FailureKind Expected,
    FixOutcome Outcome,
    EvalVerdict Verdict,
    string TriageSource,
    int Attempts,
    int ModelCalls,
    TimeSpan Duration);

public sealed record EvalSummary(string Model, IReadOnlyList<EvalCaseResult> Cases)
{
    public int Correct => Cases.Count(c => c.Verdict is EvalVerdict.CorrectFix or EvalVerdict.CorrectDefect);
    public int FalseFixes => Cases.Count(c => c.Verdict == EvalVerdict.FalseFix);
    public int WronglyBlocked => Cases.Count(c => c.Verdict == EvalVerdict.WronglyBlocked);
    public int DecidedWithoutModel => Cases.Count(c => c.ModelCalls == 0);
    public int ModelCalls => Cases.Sum(c => c.ModelCalls);
    public TimeSpan Duration => TimeSpan.FromTicks(Cases.Sum(c => c.Duration.Ticks));

    /// <summary>The two harmful outcomes. A pipeline should fail on either.</summary>
    public bool HasHarmfulResult => FalseFixes > 0 || WronglyBlocked > 0;
}

/// <summary>
/// The eval harness: the same failures, with known answers, run the same way
/// every time. It is how you find out whether a change to a prompt, a rule, a
/// guardrail or the model made things better or worse, instead of guessing.
/// </summary>
public static class EvalRunner
{
    public static async Task<EvalSummary> RunAsync(
        string modelName,
        IEnumerable<EvalCase> cases,
        Func<EvalCase, FixHarnessRunner> harnessFor,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EvalCaseResult>();
        foreach (var evalCase in cases)
        {
            log?.Invoke($"\n=== {evalCase.Id} (expected: {evalCase.Expected}) ===");
            var result = await harnessFor(evalCase).RunAsync(evalCase.Request, cancellationToken);
            var verdict = Judge(evalCase.Expected, result.Outcome);
            log?.Invoke($"=== {evalCase.Id}: {result.Outcome} -> {verdict}");
            results.Add(new EvalCaseResult(
                evalCase.Id, evalCase.Expected, result.Outcome, verdict, result.Triage.Source,
                result.Attempts.Count, result.ModelCalls, result.Duration));
        }
        return new EvalSummary(modelName, results);
    }

    public static EvalVerdict Judge(FailureKind expected, FixOutcome outcome) => (expected, outcome) switch
    {
        (_, FixOutcome.NotReproduced or FixOutcome.NoTestsMatched or FixOutcome.CouldNotRun) => EvalVerdict.Invalid,
        (FailureKind.TestIssue, FixOutcome.Fixed) => EvalVerdict.CorrectFix,
        (FailureKind.TestIssue, FixOutcome.ProductDefect) => EvalVerdict.WronglyBlocked,
        (FailureKind.TestIssue, _) => EvalVerdict.MissedFix,
        (FailureKind.ProductDefect, FixOutcome.ProductDefect) => EvalVerdict.CorrectDefect,
        (FailureKind.ProductDefect, FixOutcome.Fixed) => EvalVerdict.FalseFix,
        _ => EvalVerdict.DefectNotRecognised,
    };

    public static string ToMarkdown(EvalSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine($"## FixHarness eval: {summary.Model}");
        text.AppendLine();
        text.AppendLine("| Failure | Expected | Outcome | Verdict | Decided by | Attempts | Model calls | Seconds |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in summary.Cases)
        {
            text.AppendLine($"| {c.Id} | {c.Expected} | {c.Outcome} | {c.Verdict} | {c.TriageSource} | {c.Attempts} | {c.ModelCalls} | {c.Duration.TotalSeconds:0.0} |");
        }
        text.AppendLine();
        text.AppendLine($"- **Correct:** {summary.Correct} of {summary.Cases.Count}");
        text.AppendLine($"- **False fixes** (product defect hidden by a test change): {summary.FalseFixes}");
        text.AppendLine($"- **Wrongly blocked** (test issue reported as a product defect): {summary.WronglyBlocked}");
        text.AppendLine($"- **Decided without the model:** {summary.DecidedWithoutModel} of {summary.Cases.Count}");
        text.AppendLine($"- **Model calls:** {summary.ModelCalls}");
        text.AppendLine($"- **Total time:** {summary.Duration.TotalSeconds:0.0} s");
        return text.ToString();
    }
}
