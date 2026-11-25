using System.Text.Json;
using TestAgentHarness.Eval;
using TestAgentHarness.Harness;
using TestAgentHarness.Model;
using TestAgentHarness.Triage;

namespace TestAgentHarness.SelfTests;

/// <summary>Tests for the eval runner, using every planted failure in the repository.</summary>
public static class EvalTests
{
    public static void Register(Suite suite)
    {
        suite.Add("eval: judges each outcome against the known answer", () =>
        {
            Check.Equal(EvalVerdict.CorrectFix, EvalRunner.Judge(FailureKind.TestIssue, FixOutcome.Fixed), "test issue fixed");
            Check.Equal(EvalVerdict.WronglyBlocked, EvalRunner.Judge(FailureKind.TestIssue, FixOutcome.ProductDefect), "test issue called a defect");
            Check.Equal(EvalVerdict.MissedFix, EvalRunner.Judge(FailureKind.TestIssue, FixOutcome.NotFixed), "test issue not fixed");
            Check.Equal(EvalVerdict.CorrectDefect, EvalRunner.Judge(FailureKind.ProductDefect, FixOutcome.ProductDefect), "defect reported");
            Check.Equal(EvalVerdict.FalseFix, EvalRunner.Judge(FailureKind.ProductDefect, FixOutcome.Fixed), "defect hidden by a test change");
            Check.Equal(EvalVerdict.DefectNotRecognised, EvalRunner.Judge(FailureKind.ProductDefect, FixOutcome.NotFixed), "defect not recognised");
            Check.Equal(EvalVerdict.Invalid, EvalRunner.Judge(FailureKind.TestIssue, FixOutcome.NotReproduced), "not reproduced");
        });

        suite.Add("eval: the five UI planted failures come out right with the scripted models", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var summary = await EvalRunner.RunAsync("scripted", Cases(repo), evalCase =>
            {
                var script = Path.Combine(repo.Path, "failures", "scripts", evalCase.Id + ".json");
                IModelClient model = File.Exists(script) ? ScriptedModelClient.FromFile(script) : new HarnessTests.ThrowingModel();
                return new FixRunner(model, new TinyShopSimulator());
            });

            Check.Equal(5, summary.Cases.Count, "cases");
            Check.Equal(5, summary.Correct, "correct");
            Check.Equal(3, summary.Cases.Count(c => c.Verdict == EvalVerdict.CorrectFix), "test issues fixed");
            Check.Equal(2, summary.Cases.Count(c => c.Verdict == EvalVerdict.CorrectDefect), "product defects reported");
            Check.Equal(2, summary.DecidedWithoutModel, "the two defects were decided by rules alone");
            Check.True(!summary.HasHarmfulResult, "no harmful result");
            Check.Contains("| 05-checkout-crash | ProductDefect | ProductDefect | CorrectDefect | BrowserScriptError |", EvalRunner.ToMarkdown(summary), "markdown table");
            await HarnessTests.AssertRepoUntouched(repo);
        });

        suite.Add("eval: catches a setup that reports test issues as product defects", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            // No rules, and a model that blames the product for everything.
            var summary = await EvalRunner.RunAsync("blames-the-product", Cases(repo), _ =>
                new FixRunner(HarnessTests.Script(HarnessTests.Say("The product is broken.\nVERDICT: PRODUCT_DEFECT")), new TinyShopSimulator())
                {
                    TriageRules = [],
                });

            Check.Equal(3, summary.WronglyBlocked, "test issues wrongly blocked");
            Check.Equal(0, summary.FalseFixes, "false fixes");
            Check.True(summary.HasHarmfulResult, "the eval flags the run as harmful");
        });
    }

    private static List<EvalCase> Cases(TempRepo repo)
    {
        using var manifest = JsonDocument.Parse(repo.Read("failures/manifest.json"));
        // The UI failures only: they run on the fast simulator. The API failures
        // run against the real API in BackendTests and in the capability report.
        return manifest.RootElement.EnumerateArray().Where(entry => !entry.TryGetProperty("testProject", out _)).Select(entry =>
        {
            var id = entry.GetProperty("id").GetString()!;
            var expected = entry.GetProperty("kind").GetString() == "product-defect" ? FailureKind.ProductDefect : FailureKind.TestIssue;
            return new EvalCase(id, expected, TriageTests.Request(repo, id));
        }).ToList();
    }
}
