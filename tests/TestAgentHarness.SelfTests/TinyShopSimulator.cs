using System.Text.RegularExpressions;
using TestAgentHarness.Testing;

namespace TestAgentHarness.SelfTests;

/// <summary>
/// Stands in for "dotnet test" on the TinyShop sample so the harness can be
/// tested end to end without NuGet packages or a browser. It reads the files in
/// the workspace and works out what each scenario would do: a stale locator
/// times out, a too-early read sees the old total, a missing binding is
/// inconclusive, and so on. Failure messages imitate the real ones.
/// </summary>
public sealed class TinyShopSimulator : ITestRunner
{
    private const string Prefix = "TinyShop.Tests.Features.";

    /// <summary>How many times tests were run, to prove when the model or the runner was skipped.</summary>
    public int Runs { get; private set; }

    /// <summary>When set, the target test fails on this (1-based) run only: a flaky test.</summary>
    public int? FailOnRun { get; set; }

    public Task<TestRunResult> RunAsync(string workingDirectory, string filter, CancellationToken cancellationToken = default)
    {
        Runs++;
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([workingDirectory, "sample", .. parts]));

        var html = Read("TinyShop.Web", "index.html");
        var js = Read("TinyShop.Web", "app.js");
        var page = Read("TinyShop.Tests", "Pages", "ShopPage.cs");
        var steps = Read("TinyShop.Tests", "Steps", "ShopSteps.cs");
        var basketFeature = Read("TinyShop.Tests", "Features", "Basket.feature");
        var checkoutFeature = Read("TinyShop.Tests", "Features", "Checkout.feature");

        if (steps.Contains("COMPILE_ERROR") || page.Contains("COMPILE_ERROR"))
        {
            return Task.FromResult(new TestRunResult(false, [], "error CS0103: The name 'COMPILE_ERROR' does not exist in the current context"));
        }

        string LocatorOf(string property) =>
            Regex.Match(page, property + @"\s*=>\s*page\.Locator\(""\[data-testid='([^']+)'\]""\)").Groups[1].Value;
        bool Exists(string testId) => html.Contains($"data-testid=\"{testId}\"");
        TestCaseResult Timeout(string name, string testId) => new(name, TestOutcome.Failed,
            $"System.TimeoutException : Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"[data-testid='{testId}']\")",
            "at TinyShop.Tests.Steps.ShopSteps.ThenTheBasketTotalIs(String expected) in ShopSteps.cs:line 48");
        TestCaseResult Mismatch(string name, string expected, string actual) => new(name, TestOutcome.Failed,
            $"  Expected string length {expected.Length} but was {actual.Length}.\n  Expected: \"{expected}\"\n  But was:  \"{actual}\"",
            "at TinyShop.Tests.Steps.ShopSteps.ThenTheBasketTotalIs(String expected) in ShopSteps.cs:line 49");

        var expectedTotals = Regex.Matches(basketFeature, "Then the basket total is \"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var totalId = LocatorOf("BasketTotal");
        var readsTooEarly = js.Contains("setTimeout") && steps.Contains("BasketTotal.InnerTextAsync");

        TestCaseResult Total(string name, int index, string realValue)
        {
            if (!Exists(totalId)) return Timeout(name, totalId);
            var actual = totalId == "basket-count" ? "2" : readsTooEarly ? "£0.00" : realValue;
            var expected = index < expectedTotals.Count ? expectedTotals[index] : "(missing)";
            return actual == expected ? new TestCaseResult(name, TestOutcome.Passed) : Mismatch(name, expected, actual);
        }

        TestCaseResult Checkout(string name)
        {
            var stepText = checkoutFeature.Contains("I place the order") ? "I place the order" : "I check out";
            if (!steps.Contains($"\"{stepText}\""))
            {
                return new TestCaseResult(name, TestOutcome.NotRun,
                    $"No matching step definition found for the step. Use the following code to create one:\n[When(\"{stepText}\")]");
            }
            if (js.Contains("order.items"))
            {
                return new TestCaseResult(name, TestOutcome.Failed,
                    "Microsoft.Playwright.PlaywrightException : Locator expected to have text 'Order placed: 1 item, total £1.20'\nBut was: ''\nCall log:\n  - Expect \"ToHaveTextAsync\" with timeout 5000ms\n  - waiting for Locator(\"[data-testid='confirmation']\")",
                    "at TinyShop.Tests.Steps.ShopSteps.ThenISeeTheConfirmation(String expected) in ShopSteps.cs:line 55",
                    "[browser] pageerror: ReferenceError: order is not defined\n    at HTMLButtonElement.<anonymous> (app.js:39)");
            }
            return new TestCaseResult(name, TestOutcome.Passed);
        }

        var all = new List<TestCaseResult>
        {
            Exists(LocatorOf("BasketCount"))
                ? new(Prefix + "BasketFeature.AddingAnItemShowsItInTheBasket", TestOutcome.Passed)
                : Timeout(Prefix + "BasketFeature.AddingAnItemShowsItInTheBasket", LocatorOf("BasketCount")),
            Total(Prefix + "BasketFeature.BasketTotalAddsUp", 0, "£3.50"),
            Total(Prefix + "BasketFeature.DiscountCodeReducesTheTotal", 1, js.Contains("subtotal - discountPercent") ? "£2.90" : "£2.70"),
            Checkout(Prefix + "CheckoutFeature.CheckingOutShowsAConfirmation"),
        };

        var wanted = filter.StartsWith("FullyQualifiedName~", StringComparison.Ordinal) ? filter["FullyQualifiedName~".Length..] : "\0";
        var matched = all.Where(t => t.Name.Contains(wanted, StringComparison.Ordinal)).ToList();

        if (FailOnRun == Runs && matched.Count == 1)
        {
            matched = [new TestCaseResult(matched[0].Name, TestOutcome.Failed, "Simulated intermittent failure.")];
        }

        return Task.FromResult(new TestRunResult(true, matched, ""));
    }
}
