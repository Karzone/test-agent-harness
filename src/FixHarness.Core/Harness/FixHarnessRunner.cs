using FixHarness.Context;
using FixHarness.Guardrails;
using FixHarness.Loop;
using FixHarness.Memory;
using FixHarness.Model;
using FixHarness.Testing;
using FixHarness.Tools;
using FixHarness.Triage;

namespace FixHarness.Harness;

/// <summary>What to fix: a repository, the failing test and where test code lives.</summary>
/// <param name="ChangeDiff">The diff that preceded the failure (the pull request). Evidence for triage and context for the model.</param>
public sealed record FixRequest(
    string RepoPath,
    string Filter,
    IReadOnlyList<string> TestRoots,
    string? PlantPatch = null,
    string? ChangeNote = null,
    string? NeighbourFilter = null,
    int MaxAttempts = 3,
    int VerifyRuns = 3,
    string? ChangeDiff = null);

public enum FixOutcome
{
    /// <summary>A patch exists and the verifier accepted it.</summary>
    Fixed,
    /// <summary>Every attempt was rejected by the verifier. No patch is offered.</summary>
    NotFixed,
    /// <summary>The test is right and the product is wrong. A defect report is produced instead of a patch.</summary>
    ProductDefect,
    /// <summary>The test passed before anything was changed.</summary>
    NotReproduced,
    /// <summary>The filter matched no tests, so there was nothing to judge.</summary>
    NoTestsMatched,
}

public sealed record Attempt(int Number, LoopResult Loop, string Patch, VerifyResult Verification);

public sealed record FixResult(
    FixOutcome Outcome,
    string Summary,
    string Filter,
    string Model,
    TestRunResult Baseline,
    FailureSignature? Signature,
    TriageVerdict Triage,
    IReadOnlyList<Attempt> Attempts,
    string? Patch,
    bool ReplayedFromMemory,
    IReadOnlyList<string> LessonsUsed,
    TimeSpan Duration)
{
    public int ModelCalls => Attempts.Sum(a => a.Loop.Iterations);
}

/// <summary>
/// The harness owns the whole lifecycle of a run:
///
///   open a throwaway workspace
///   -> confirm the test really fails
///   -> triage: a person's label first, then rules           (may end here: product defect)
///   -> replay a remembered fix for this exact failure       (may end here: fixed, no model call)
///   -> agent loop with tools, guardrails and lessons from similar failures
///   -> verify; on rejection reset the workspace and retry with the reason
///   -> remember what was learned, remove the workspace
///
/// The model is the last resort, not the first step, and only a verified patch
/// ever leaves the harness.
/// </summary>
public sealed class FixHarnessRunner(IModelClient model, ITestRunner testRunner, Action<string>? log = null)
{
    public IReadOnlyList<TriageRule> TriageRules { get; init; } = FailureTriage.DefaultRules;

    /// <summary>Optional. Without it every run starts from nothing, which is what the eval wants.</summary>
    public FixMemory? Memory { get; init; }

    public async Task<FixResult> RunAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var policy = new PatchPolicy(new PatchPolicyOptions(request.TestRoots));
        var verifier = new Verifier(testRunner, policy, request.VerifyRuns, log);

        var attempts = new List<Attempt>();
        var rejections = new List<string>();
        IReadOnlyList<string> lessons = [];
        FailureSignature? signature = null;
        var triage = TriageVerdict.Unknown;
        IReadOnlySet<string> neighboursFailingBefore = new HashSet<string>();

        await using var workspace = await Workspace.CreateAsync(request.RepoPath, request.PlantPatch);
        log?.Invoke($"[harness] workspace: {workspace.Path}");

        // ── 1. Reproduce ─────────────────────────────────────────────────────
        log?.Invoke("[harness] running the test to capture the failure...");
        var baseline = await testRunner.RunAsync(workspace.Path, request.Filter, cancellationToken);
        if (baseline.NoTestsMatched) return Finish(FixOutcome.NoTestsMatched, "The filter matched no tests.");
        if (baseline.AllPassed) return Finish(FixOutcome.NotReproduced, "The test passes without any change.");
        signature = FailureSignature.From(request.Filter, baseline);

        // ── 2. Triage: a person's label outranks the rules, the rules outrank the model ──
        if (Memory?.HumanLabel(signature) is { Label: { } labelled } label)
        {
            triage = new TriageVerdict(labelled, "human label", label.Lesson.Length > 0 ? label.Lesson : "A person labelled this exact failure.");
        }
        else
        {
            var changedPaths = request.ChangeDiff is null ? [] : GitChanges.PathsInPatch(request.ChangeDiff);
            triage = FailureTriage.Classify(new FailureEvidence(baseline, changedPaths, request.TestRoots, request.ChangeNote), TriageRules);
        }
        log?.Invoke($"[harness] triage: {triage.Kind} ({triage.Source}) - {triage.Reason}");

        if (triage.Kind == FailureKind.ProductDefect)
        {
            return Finish(FixOutcome.ProductDefect, $"Product defect, decided by {triage.Source} without calling the model. {triage.Reason}");
        }

        if (!string.IsNullOrWhiteSpace(request.NeighbourFilter))
        {
            log?.Invoke("[harness] recording which neighbouring tests already fail...");
            var neighbours = await testRunner.RunAsync(workspace.Path, request.NeighbourFilter, cancellationToken);
            neighboursFailingBefore = neighbours.NotPassedNames.ToHashSet();
        }

        Task<VerifyResult> Verify() =>
            verifier.VerifyAsync(workspace, request.Filter, request.NeighbourFilter, neighboursFailingBefore, cancellationToken);

        // ── 3. Replay: seen this exact failure before? Try the fix that worked. ──
        if (Memory?.FindReplay(signature) is { } remembered)
        {
            log?.Invoke($"[memory] this exact failure was fixed before ({remembered.CreatedUtc:yyyy-MM-dd}); replaying that fix...");
            var replay = await workspace.TryApplyPatchAsync(remembered.Patch!)
                ? await Verify()
                : new VerifyResult(false, "The remembered patch no longer applies.", []);
            Memory.RecordReplay(remembered, replay.Passed);

            if (replay.Passed)
            {
                return Finish(
                    FixOutcome.Fixed,
                    $"Replayed a remembered fix and verified it again, with no model call: {replay.Reason}.",
                    await workspace.DiffAsync(),
                    replayed: true);
            }

            log?.Invoke($"[memory] the remembered fix was rejected ({AgentLoop.OneLine(replay.Reason, 100)}); falling back to the model");
            await workspace.ResetAsync();
        }

        // ── 4. Agent loop, with what was learned from similar failures ────────
        lessons = Memory?.Lessons(signature) ?? [];
        if (lessons.Count > 0) log?.Invoke($"[memory] {lessons.Count} lesson(s) from similar failures added to the context");

        for (var number = 1; number <= request.MaxAttempts; number++)
        {
            if (number > 1) await workspace.ResetAsync();
            log?.Invoke($"[harness] attempt {number} of {request.MaxAttempts}");

            var tools = WorkspaceTools.Create(workspace.Path, testRunner, request.Filter, policy);
            var messages = ContextBuilder.Create(new TaskContext(
                request.Filter, baseline.Describe(), request.ChangeNote, request.ChangeDiff, triage, rejections, lessons));
            var loop = await AgentLoop.RunAsync(model, messages, tools, LoopGuardrails.Default, log, cancellationToken);
            var patch = await workspace.DiffAsync();

            if (FailureTriage.ParseModelVerdict(loop.Answer) == FailureKind.ProductDefect)
            {
                if (triage.Kind == FailureKind.TestIssue)
                {
                    // There is hard evidence that this is a test issue. A model that
                    // disagrees does not get to stop the fix; it is told why and tries again.
                    var disagreement = $"You concluded this is a product defect, but the evidence says it is a test issue ({triage.Source}): {triage.Reason} Fix the test.";
                    attempts.Add(new Attempt(number, loop, patch, new VerifyResult(false, disagreement, [])));
                    rejections.Add(disagreement);
                    log?.Invoke("[harness] the model said product defect; overruled by triage evidence, retrying");
                    continue;
                }

                // Nothing the model changed is kept: a defect verdict means the test was right.
                // Nothing is remembered either. A model's opinion is not evidence.
                attempts.Add(new Attempt(number, loop, "", new VerifyResult(false, "Not verified: the model concluded the product is at fault.", [])));
                triage = new TriageVerdict(FailureKind.ProductDefect, "model", loop.Answer.Trim());
                return Finish(FixOutcome.ProductDefect, "Product defect, in the model's judgement (no rule matched, so a person should confirm).");
            }

            var verification = await Verify();
            attempts.Add(new Attempt(number, loop, patch, verification));
            log?.Invoke($"[harness] verify: {(verification.Passed ? "PASS" : "FAIL")} - {AgentLoop.OneLine(verification.Reason, 160)}");

            if (verification.Passed)
            {
                // ── 5. Learn: only from a fix the verifier accepted ───────────
                Memory?.RecordFix(signature, request.Filter, LessonFrom(loop.Answer), patch, GitChanges.PathsInPatch(patch));
                return Finish(FixOutcome.Fixed, $"Verified fix on attempt {number}: {verification.Reason}.", patch);
            }

            rejections.Add(verification.Reason);
            Memory?.RecordRejection(signature, request.Filter, verification.Reason);
        }

        return Finish(FixOutcome.NotFixed, $"No verified fix after {attempts.Count} attempt(s). Last rejection: {rejections.Last()}");

        FixResult Finish(FixOutcome outcome, string summary, string? patch = null, bool replayed = false) =>
            new(outcome, summary, request.Filter, model.Name, baseline, signature, triage, attempts, patch, replayed, lessons, clock.Elapsed);
    } // the workspace is removed here, even if anything above threw

    /// <summary>The model's explanation without its verdict line, kept as the lesson for next time.</summary>
    private static string LessonFrom(string answer) =>
        string.Join(' ', answer.Split('\n').Where(l => !l.TrimStart().StartsWith("VERDICT", StringComparison.OrdinalIgnoreCase))).Trim();
}
