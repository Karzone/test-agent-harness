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
    string? ChangeNote = null);

public sealed record HarnessRun(string Filter, string Model, TestRunResult Baseline, LoopResult? Loop, string Patch);

/// <summary>
/// The harness owns the whole lifecycle of a run:
///
///   open a throwaway workspace -> confirm the test really fails -> build the
///   tools and context -> run the loop -> collect the patch -> remove the workspace
///
/// The tools do not know where the workspace came from, and the model does not
/// know it is temporary. Managing that is the harness's job.
/// </summary>
public sealed class FixHarnessRunner(IModelClient model, ITestRunner testRunner, Action<string>? log = null)
{
    public async Task<HarnessRun> RunAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        await using var workspace = await Workspace.CreateAsync(request.RepoPath, request.PlantPatch);
        log?.Invoke($"[harness] workspace: {workspace.Path}");

        log?.Invoke("[harness] running the test to capture the failure...");
        var baseline = await testRunner.RunAsync(workspace.Path, request.Filter, cancellationToken);
        if (baseline.AllPassed || baseline.NoTestsMatched)
        {
            // Nothing to fix, or nothing ran. Either way the model is not needed.
            return new HarnessRun(request.Filter, model.Name, baseline, Loop: null, Patch: "");
        }

        var policy = new PatchPolicy(new PatchPolicyOptions(request.TestRoots));
        var tools = WorkspaceTools.Create(workspace.Path, testRunner, request.Filter, policy);
        var messages = ContextBuilder.Create(request.Filter, baseline.Describe(), request.ChangeNote);

        var loop = await AgentLoop.RunAsync(model, messages, tools, LoopGuardrails.Default, log, cancellationToken);
        var patch = await workspace.DiffAsync();

        return new HarnessRun(request.Filter, model.Name, baseline, loop, patch);
    } // the workspace is removed here, even if anything above threw
}
