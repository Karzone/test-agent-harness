using System.Text.Json;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Harness;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;
using TestAgentHarness.Triage;

namespace TestAgentHarness.SelfTests;

/// <summary>Tests for rules-first triage and for what the harness does with each verdict.</summary>
public static class TriageTests
{
    public static void Register(Suite suite)
    {
        suite.Add("triage: each rule recognises its evidence", () =>
        {
            Verdict("No matching step definition found for the step", [], null, FailureKind.TestIssue, "MissingStepBinding");
            Verdict("Timeout 5000ms exceeded", [], null, FailureKind.ProductDefect, "BrowserScriptError", stdOut: "[browser] pageerror: ReferenceError: order is not defined");
            Verdict("Request failed with status code 503", ["src/api.cs"], null, FailureKind.ProductDefect, "ServerError");
            Verdict("  Expected: \"a\"\n  But was:  \"b\"", ["tests/Steps.cs"], null, FailureKind.TestIssue, "OnlyTestCodeChanged");
            Verdict("  Expected: \"a\"\n  But was:  \"b\"", ["src/app.js"], "Refactor. No behaviour change intended.", FailureKind.ProductDefect, "BehaviourChangedByRefactor");
            Verdict("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"#x\")", ["src/index.html"], null, FailureKind.TestIssue, "LocatorTimeoutAfterMarkupChange");
        });

        suite.Add("triage: unclear evidence is Unknown, never a guess", () =>
        {
            Verdict("  Expected: \"a\"\n  But was:  \"b\"", ["src/app.js"], "Totals now arrive later. Intended change.", FailureKind.Unknown, "none");
            Verdict("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"#x\")", ["src/app.js"], null, FailureKind.Unknown, "none");
            Verdict("something odd happened", [], null, FailureKind.Unknown, "none");
        });

        suite.Add("triage: a script error outranks the locator timeout it causes", () =>
        {
            Verdict("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"#x\")", ["src/index.html"], null,
                FailureKind.ProductDefect, "BrowserScriptError", stdOut: "[browser] pageerror: TypeError: x is undefined");
        });

        suite.Add("triage: reads the model's verdict line, and silence is not a verdict", () =>
        {
            Check.Equal(FailureKind.ProductDefect, FailureTriage.ParseModelVerdict("The total is wrong.\nVERDICT: PRODUCT_DEFECT"), "defect");
            Check.Equal(FailureKind.TestIssue, FailureTriage.ParseModelVerdict("Fixed the locator.\nverdict: test_issue"), "test issue");
            Check.Equal(FailureKind.Unknown, FailureTriage.ParseModelVerdict("I think the product might be broken."), "no verdict line");
        });

        suite.Add("policy: a fix may not rewrite what a Then step expects", () =>
        {
            const string feature = "sample/TinyShop.Tests/Features/Basket.feature";
            const string before = "  Scenario: A\n    When I add \"Coffee\" to the basket\n    Then the basket total is \"£2.70\"\n";
            var policy = new PatchPolicy(new PatchPolicyOptions(HarnessTests.TestRoots));

            var cheat = policy.Check(new Dictionary<string, FileChange> { [feature] = new(before, before.Replace("£2.70", "£2.90")) });
            Check.True(cheat.Any(v => v.Contains("expected outcome in a Then step")), "changing the expected value is rejected");

            var reworded = policy.Check(new Dictionary<string, FileChange> { [feature] = new(before, before.Replace("I add \"Coffee\"", "I put \"Coffee\"")) });
            Check.Equal(0, reworded.Count, "rewording a When step is allowed");

            var relaxed = new PatchPolicy(new PatchPolicyOptions(HarnessTests.TestRoots, AllowExpectationChanges: true));
            Check.Equal(0, relaxed.Check(new Dictionary<string, FileChange> { [feature] = new(before, before.Replace("£2.70", "£2.90")) }).Count, "can be switched off");
        });

        suite.Add("harness: a script error is reported as a product defect with no model call (failure 05)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new HarnessTests.ThrowingModel(), new TinyShopSimulator())
                .RunAsync(Request(repo, "05-checkout-crash"));

            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome");
            Check.Equal("BrowserScriptError", result.Triage.Source, "rule");
            Check.Equal(0, result.ModelCalls, "model calls");
            Check.True(result.Patch is null, "no patch for a product defect");
            Check.Contains("order is not defined", ReportWriter.ToMarkdown(result), "the defect report carries the evidence");
            await HarnessTests.AssertRepoUntouched(repo);
        });

        suite.Add("harness: a refactor that changed behaviour is a product defect (failure 04)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new HarnessTests.ThrowingModel(), new TinyShopSimulator())
                .RunAsync(Request(repo, "04-wrong-discount"));
            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome");
            Check.Equal("BehaviourChangedByRefactor", result.Triage.Source, "rule");
        });

        suite.Add("harness: without the rule, the model still cannot 'fix' a defect by changing the expectation (failure 04)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = HarnessTests.Script(
                HarnessTests.Edit("sample/TinyShop.Tests/Features/Basket.feature", "Then the basket total is \"£2.70\"", "Then the basket total is \"£2.90\""),
                HarnessTests.Say("Updated the expected total. Fixed.\nVERDICT: TEST_ISSUE"));

            // No pull-request note, so no rule fires and the model is asked.
            var result = await new FixRunner(model, new TinyShopSimulator())
                .RunAsync(Request(repo, "04-wrong-discount") with { ChangeNote = null, MaxAttempts = 1 });

            Check.Equal(FailureKind.Unknown, result.Triage.Kind, "triage");
            Check.Contains("Rejected by guardrail", result.Attempts[0].Loop.ToolEvents.Single().Result, "the edit was rejected");
            Check.Equal(FixOutcome.NotFixed, result.Outcome, "outcome");
        });

        suite.Add("harness: when the rules cannot tell, the model's defect verdict is accepted but flagged (failure 04)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = HarnessTests.Script(HarnessTests.Say("SAVE10 takes 10p off instead of 10%. The test is right.\nVERDICT: PRODUCT_DEFECT"));
            var result = await new FixRunner(model, new TinyShopSimulator())
                .RunAsync(Request(repo, "04-wrong-discount") with { ChangeNote = null });

            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome");
            Check.Equal("model", result.Triage.Source, "verdict source");
            Check.Contains("model's judgement", ReportWriter.ToMarkdown(result), "the report says a person should confirm");
        });

        suite.Add("harness: a model that calls a proven test issue a product defect is overruled and retried (failure 01)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = new ScriptedModelClient(
            [
                [HarnessTests.Say("The total element is missing. The product is broken.\nVERDICT: PRODUCT_DEFECT")],
                [HarnessTests.Edit("sample/TinyShop.Tests/Pages/ShopPage.cs", "[data-testid='basket-total']", "[data-testid='order-total']"), HarnessTests.Say("Fixed the locator.\nVERDICT: TEST_ISSUE")],
            ]);

            var result = await new FixRunner(model, new TinyShopSimulator()).RunAsync(Request(repo, "01-renamed-locator"));

            Check.Equal(FailureKind.TestIssue, result.Triage.Kind, "triage");
            Check.Equal("LocatorTimeoutAfterMarkupChange", result.Triage.Source, "rule");
            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.Equal(2, result.Attempts.Count, "attempts");
            Check.Contains("the evidence says it is a test issue", result.Attempts[0].Verification.Reason, "why attempt 1 was rejected");
        });

        suite.Add("harness: scripted fixes for failures 02 and 03 are verified", async () =>
        {
            foreach (var id in new[] { "02-reworded-step", "03-async-total" })
            {
                using var repo = await TempRepo.CreateAsync();
                var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", id + ".json"));
                var result = await new FixRunner(model, new TinyShopSimulator()).RunAsync(Request(repo, id));
                Check.Equal(FixOutcome.Fixed, result.Outcome, id + " outcome");
                Check.Equal(1, result.Attempts.Count, id + " attempts");
            }
        });
    }

    /// <summary>Builds the request for a planted failure the same way the CLI does.</summary>
    public static FixRequest Request(TempRepo repo, string failureId)
    {
        using var manifest = JsonDocument.Parse(repo.Read("failures/manifest.json"));
        var entry = manifest.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == failureId);
        var patch = entry.GetProperty("patch").GetString()!;

        return new FixRequest(
            repo.Path,
            entry.GetProperty("filter").GetString()!,
            HarnessTests.TestRoots,
            patch,
            entry.GetProperty("prNote").GetString(),
            HarnessTests.AllTests,
            ChangeDiff: repo.Read(patch));
    }

    private static void Verdict(string error, string[] changedPaths, string? note, FailureKind expectedKind, string expectedSource, string? stdOut = null)
    {
        var baseline = new TestRunResult(true, [new TestCaseResult("t", TestOutcome.Failed, error, null, stdOut)], "");
        var verdict = FailureTriage.Classify(new FailureEvidence(baseline, changedPaths, ["tests"], note));
        Check.Equal(expectedKind, verdict.Kind, $"kind for \"{error.Split('\n')[0]}\"");
        Check.Equal(expectedSource, verdict.Source, $"rule for \"{error.Split('\n')[0]}\"");
    }
}
