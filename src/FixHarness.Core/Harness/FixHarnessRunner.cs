using FixHarness.Context;
using FixHarness.Guardrails;
using FixHarness.Loop;
using FixHarness.Model;
using FixHarness.Testing;
using FixHarness.Tools;

namespace FixHarness.Harness;

/// <summary>What to fix: a repository, the failing test and where test code lives.</summary>
public sealed record FixRequest(
    string RepoPath,
    string Filter,
    IReadOnlyList<string> TestRoots,
    string? PlantPatch = null,
    string? ChangeNote = null,
    string? NeighbourFilter = null,
    int MaxAttempts = 3,
    int VerifyRuns = 3);

public enum FixOutcome
{
    /// <summary>A patch exists and the verifier accepted it.</summary>
    Fixed,
    /// <summary>Every attempt was rejected by the verifier. No patch is offered.</summary>
    NotFixed,
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
    IReadOnlyList<Attempt> Attempts,
    string? Patch);

/// <summary>
/// The harness owns the whole lifecycle of a run:
///
///   open a throwaway workspace -> confirm the test really fails -> build the
///   tools and context -> run the loop -> verify the result -> remove the workspace
///
/// If the verifier rejects an attempt, the harness throws that workspace away
/// and starts a fresh one, telling the model why the last attempt was rejected.
/// Only a verified patch ever leaves the harness.
/// </summary>
public sealed class FixHarnessRunner(IModelClient model, ITestRunner testRunner, Action<string>? log = null)
{
    public async Task<FixResult> RunAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        var policy = new PatchPolicy(new PatchPolicyOptions(request.TestRoots));
        var verifier = new Verifier(testRunner, policy, request.VerifyRuns, log);

        var attempts = new List<Attempt>();
        var rejections = new List<string>();
        TestRunResult? baseline = null;
        IReadOnlySet<string> neighboursFailingBefore = new HashSet<string>();

        for (var number = 1; number <= request.MaxAttempts; number++)
        {
            await using var workspace = await Workspace.CreateAsync(request.RepoPath, request.PlantPatch);
            log?.Invoke($"[harness] attempt {number} of {request.MaxAttempts}, workspace: {workspace.Path}");

            if (baseline is null)
            {
                log?.Invoke("[harness] running the test to capture the failure...");
                baseline = await testRunner.RunAsync(workspace.Path, request.Filter, cancellationToken);

                // Neither case needs the model.
                if (baseline.NoTestsMatched) return Finish(FixOutcome.NoTestsMatched, "The filter matched no tests.");
                if (baseline.AllPassed) return Finish(FixOutcome.NotReproduced, "The test passes without any change.");

                if (!string.IsNullOrWhiteSpace(request.NeighbourFilter))
                {
                    log?.Invoke("[harness] recording which neighbouring tests already fail...");
                    var neighbours = await testRunner.RunAsync(workspace.Path, request.NeighbourFilter, cancellationToken);
                    neighboursFailingBefore = neighbours.NotPassedNames.ToHashSet();
                }
            }

            var tools = WorkspaceTools.Create(workspace.Path, testRunner, request.Filter, policy);
            var messages = ContextBuilder.Create(request.Filter, baseline.Describe(), request.ChangeNote, rejections);
            var loop = await AgentLoop.RunAsync(model, messages, tools, LoopGuardrails.Default, log, cancellationToken);

            var patch = await workspace.DiffAsync();
            var verification = await verifier.VerifyAsync(
                workspace, request.Filter, request.NeighbourFilter, neighboursFailingBefore, cancellationToken);
            attempts.Add(new Attempt(number, loop, patch, verification));
            log?.Invoke($"[harness] verify: {(verification.Passed ? "PASS" : "FAIL")} - {AgentLoop.OneLine(verification.Reason, 160)}");

            if (verification.Passed)
            {
                return Finish(FixOutcome.Fixed, $"Verified fix on attempt {number}: {verification.Reason}.", patch);
            }

            rejections.Add(verification.Reason);
        } // each workspace is removed at the end of its attempt, even on an exception

        return Finish(FixOutcome.NotFixed, $"No verified fix after {attempts.Count} attempt(s). Last rejection: {rejections.Last()}");

        FixResult Finish(FixOutcome outcome, string summary, string? patch = null) =>
            new(outcome, summary, request.Filter, model.Name, baseline!, attempts, patch);
    }
}
