using TestAgentHarness.Eval;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Harness;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;
using TestAgentHarness.Triage;

namespace TestAgentHarness.SelfTests;

/// <summary>
/// Tests for backend (API) support: the environment verdict, the backend triage
/// rules, the expected-value guard, and end-to-end runs that really compile the
/// test client and really call the sample API.
/// </summary>
public static class BackendTests
{
    private static readonly string[] ApiTestRoots = ["sample/TinyShop.ApiTests"];

    public static void Register(Suite suite)
    {
        suite.Add("triage: an unreachable system is an environment problem, not a test issue or a defect", () =>
        {
            foreach (var message in new[]
            {
                "System.Net.Http.HttpRequestException : Connection refused (localhost:5001)",
                "No connection could be made because the target machine actively refused it. (10.0.0.4:443)",
                "System.Net.Http.HttpRequestException : No such host is known. (api.test.internal:443)",
                "Response status code does not indicate success: 503 (Service Unavailable).",
                "Response status code does not indicate success: 502 (Bad Gateway).",
                "  Expected: OK\n  But was:  GatewayTimeout",
                "The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.",
                "net::ERR_CONNECTION_REFUSED at http://localhost:3000/",
            })
            {
                var verdict = Classify(message, ["src/Api/Program.cs"]);
                Check.Equal(FailureKind.Environment, verdict.Kind, $"kind for \"{message.Split('\n')[0]}\"");
            }
        });

        suite.Add("triage: a 500 is a product defect, with or without the status text", () =>
        {
            Check.Equal("ServerError", Classify("Response status code does not indicate success: 500 (Internal Server Error).", []).Source, "status text");
            Check.Equal("ServerError", Classify("  Expected: OK\n  But was:  InternalServerError", []).Source, "enum name, as NUnit prints it");
        });

        suite.Add("triage: a 404 after a route change is a test issue; a 404 with no route change is left to the model", () =>
        {
            const string notFound = "System.Net.Http.HttpRequestException : Response status code does not indicate success: 404 (Not Found).";
            const string routeDiff = "--- a/src/Api/ShopApi.cs\n+++ b/src/Api/ShopApi.cs\n@@ -1 +1 @@\n-        app.MapPost(\"/baskets/{id}/discount\", x);\n+        app.MapPost(\"/baskets/{id}/discounts\", x);\n";
            const string otherDiff = "--- a/src/Api/ShopApi.cs\n+++ b/src/Api/ShopApi.cs\n@@ -1 +1 @@\n-        var total = 1;\n+        var total = 2;\n";

            var moved = Classify(notFound, ["src/Api/ShopApi.cs"], diff: routeDiff);
            Check.Equal(FailureKind.TestIssue, moved.Kind, "kind when a route moved");
            Check.Equal("NotFoundAfterRouteChange", moved.Source, "rule");
            Check.Contains("/baskets/{id}/discount", moved.Reason, "the reason quotes the route line");

            Check.Equal(FailureKind.Unknown, Classify(notFound, ["src/Api/ShopApi.cs"], diff: otherDiff).Kind, "kind when no route changed");
        });

        suite.Add("triage: the model can also conclude 'environment'", () =>
        {
            Check.Equal(FailureKind.Environment, FailureTriage.ParseModelVerdict("The API host is down.\nVERDICT: ENVIRONMENT"), "verdict");
        });

        suite.Add("policy: a fix may not rewrite an expected value in a C# assertion", () =>
        {
            const string test = "sample/TinyShop.ApiTests/BasketApiTests.cs";
            const string before = "        Assert.That(basket.Items, Has.Length.EqualTo(2));\n        Assert.That(basket.TotalPence, Is.EqualTo(350));\n";
            var policy = new PatchPolicy(new PatchPolicyOptions(ApiTestRoots));

            Check.True(Violations(policy, test, before, before.Replace("EqualTo(350)", "EqualTo(0)")).Any(v => v.Contains("rewrites an expected value") && v.Contains("350")), "changing 350 is rejected");
            Check.True(Violations(policy, test, "Assert.That(order.Status, Is.EqualTo(\"placed\"));", "Assert.That(order.Status, Is.EqualTo(\"failed\"));").Any(v => v.Contains("\"placed\"")), "changing a string expectation is rejected");

            Check.Equal(0, Violations(policy, test, before, before + "        Assert.That(basket.DiscountPercent, Is.EqualTo(0));\n").Count, "adding an assertion is allowed");
            Check.Equal(0, Violations(policy, test, "var b = await Shop.AddItemAsync(id, \"apple\");\n" + before, "var b = await Shop.AddItemAsync(id, \"pear\");\n" + before).Count, "changing a non-assertion line is allowed");
            Check.Equal(0, Violations(new PatchPolicy(new PatchPolicyOptions(ApiTestRoots, AllowExpectationChanges: true)), test, before, before.Replace("EqualTo(350)", "EqualTo(0)")).Count, "can be switched off");
        });

        suite.Add("harness: an unreachable API ends as an environment issue with no model call and no patch", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var down = new HarnessTests.FixedResultRunner(new TestRunResult(true,
                [new TestCaseResult("CheckoutPlacesTheOrder", TestOutcome.Failed, "System.Net.Http.HttpRequestException : Connection refused (localhost:5001)")], ""));

            var result = await new FixRunner(new HarnessTests.ThrowingModel(), down)
                .RunAsync(new FixRequest(repo.Path, "FullyQualifiedName~CheckoutApiTests", ApiTestRoots));

            Check.Equal(FixOutcome.EnvironmentIssue, result.Outcome, "outcome");
            Check.Equal(0, result.ModelCalls, "model calls");
            Check.True(result.Patch is null, "no patch");
            Check.Contains("environment problem (nothing changed)", ReportWriter.ToMarkdown(result), "report heading");
        });

        suite.Add("eval: environment results are scored without counting as harmful unless something was changed", () =>
        {
            Check.Equal(EvalVerdict.CorrectEnvironment, EvalRunner.Judge(FailureKind.Environment, FixOutcome.EnvironmentIssue), "correct");
            Check.Equal(EvalVerdict.Misclassified, EvalRunner.Judge(FailureKind.TestIssue, FixOutcome.EnvironmentIssue), "test issue called environment");
            Check.Equal(EvalVerdict.FalseFix, EvalRunner.Judge(FailureKind.Environment, FixOutcome.Fixed), "environment problem 'fixed'");
        });

        suite.Add("backend (real API): a moved endpoint is triaged by rule and fixed in the test client (failure 07)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", "07-moved-discount-route.json"));
            var result = await new FixRunner(model, new ApiProbeRunner()).RunAsync(TriageTests.Request(repo, "07-moved-discount-route"));

            Check.Equal("NotFoundAfterRouteChange", result.Triage.Source, "rule");
            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome: " + result.Summary);
            Check.Contains("+++ b/sample/TinyShop.ApiTests/ShopApiClient.cs", result.Patch!, "patch touches the test client");
            Check.True(!result.Patch!.Contains("TinyShop.Api/"), "patch does not touch the API");
            Check.Contains("5 neighbouring test(s) checked", result.Summary, "neighbours were really run");
            await HarnessTests.AssertRepoUntouched(repo);
        });

        suite.Add("backend (real API): a 500 from the API is reported as a product defect with no model call (failure 09)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new HarnessTests.ThrowingModel(), new ApiProbeRunner()).RunAsync(TriageTests.Request(repo, "09-checkout-crash-api"));

            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome: " + result.Summary);
            Check.Equal("ServerError", result.Triage.Source, "rule");
            Check.Contains("500 (Internal Server Error)", result.Baseline.Describe(), "the real failure message");
        });
    }

    private static TriageVerdict Classify(string error, string[] changedPaths, string? note = null, string? diff = null) =>
        FailureTriage.Classify(new FailureEvidence(
            new TestRunResult(true, [new TestCaseResult("t", TestOutcome.Failed, error)], ""), changedPaths, ["tests"], note, diff));

    private static IReadOnlyList<string> Violations(PatchPolicy policy, string path, string before, string after) =>
        policy.Check(new Dictionary<string, FileChange> { [path] = new(before, after) });
}
