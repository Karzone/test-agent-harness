using FixHarness.Guardrails;
using FixHarness.Testing;

namespace FixHarness.Harness;

public sealed record VerifyResult(bool Passed, string Reason, IReadOnlyList<string> Checks);

/// <summary>
/// Decides whether a fix is real. It never reads the model's answer. It looks
/// only at evidence the harness gathers itself:
///
///   1. Something was actually changed.
///   2. The whole diff obeys the patch policy (checked again, independently of the tools).
///   3. The failing test now passes, several times in a row (one green run can be luck).
///   4. No neighbouring test that was passing has started to fail.
///
/// Guardrails catch a run that goes wrong structurally. The verifier catches a
/// run that finished cleanly and is still wrong.
/// </summary>
public sealed class Verifier(ITestRunner testRunner, PatchPolicy policy, int verifyRuns = 3, Action<string>? log = null)
{
    public async Task<VerifyResult> VerifyAsync(
        Workspace workspace,
        string filter,
        string? neighbourFilter,
        IReadOnlySet<string> neighboursFailingBefore,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<string>();
        VerifyResult Fail(string reason) => new(false, reason, checks);

        // 1. Something changed.
        var changes = await workspace.ChangesAsync();
        if (changes.Count == 0) return Fail("No changes were made, so nothing can have been fixed.");
        var lines = changes.Values.Sum(c => PatchPolicy.CountChangedLines(c.Original, c.Current));
        checks.Add($"{changes.Count} file(s) changed, about {lines} line(s)");

        // 2. The final diff obeys the policy.
        var violations = policy.Check(changes);
        if (violations.Count > 0) return Fail("The final diff breaks the patch policy: " + string.Join(" ", violations));
        checks.Add("diff obeys the patch policy");

        // 3. The target test passes every time.
        for (var run = 1; run <= verifyRuns; run++)
        {
            log?.Invoke($"[verify] target test, run {run} of {verifyRuns}...");
            var result = await testRunner.RunAsync(workspace.Path, filter, cancellationToken);
            if (!result.AllPassed)
            {
                var kind = run == 1 ? "still fails" : "is flaky: it passed, then failed";
                return Fail($"The target test {kind} (run {run} of {verifyRuns}).\n{result.Describe(1200)}");
            }
        }
        checks.Add($"target test passed {verifyRuns} of {verifyRuns} runs");

        // 4. Nothing nearby got broken.
        if (!string.IsNullOrWhiteSpace(neighbourFilter))
        {
            log?.Invoke("[verify] neighbouring tests...");
            var neighbours = await testRunner.RunAsync(workspace.Path, neighbourFilter, cancellationToken);
            if (!neighbours.BuildSucceeded) return Fail("The neighbouring tests no longer build.\n" + neighbours.Describe(1200));

            var newlyBroken = neighbours.NotPassedNames.Where(name => !neighboursFailingBefore.Contains(name)).ToList();
            if (newlyBroken.Count > 0) return Fail("The fix broke tests that were passing before: " + string.Join(", ", newlyBroken));
            checks.Add($"{neighbours.Tests.Count} neighbouring test(s) checked, none newly broken");
        }

        return new VerifyResult(true, string.Join("; ", checks), checks);
    }
}
