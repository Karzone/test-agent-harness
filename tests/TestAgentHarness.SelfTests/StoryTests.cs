using TestAgentHarness.Model;
using TestAgentHarness.Story;

namespace TestAgentHarness.SelfTests;

/// <summary>
/// Tests for story checking. The logic tests use a fake browser and run
/// anywhere. The end-to-end tests drive a real Chromium through the sidecar and
/// are skipped when Node or Playwright is not available.
/// </summary>
public static class StoryTests
{
    public const string TotalSelector = "[data-testid='basket-total']";

    public static void Register(Suite suite)
    {
        suite.Add("story: a pass is only recorded when the harness itself reads the expected value", async () =>
        {
            var browser = new FakeBrowser { ["#total"] = "£2.90" };
            var tools = new StoryTools(browser, Story(new Criterion("AC1", "Total is £2.70", "£2.70")), NewDirectory());
            var registry = tools.Create();

            var claimedPass = await registry.ExecuteAsync(Call("record_result", new { criterion_id = "AC1", status = "pass", selector = "#total" }));
            Check.Contains("Not recorded: you said pass", claimedPass, "false pass rejected");
            Check.True(!tools.AllRecorded, "nothing recorded for a false pass");

            var honestFail = await registry.ExecuteAsync(Call("record_result", new { criterion_id = "AC1", status = "fail", selector = "#total" }));
            Check.Contains("Recorded AC1 as fail", honestFail, "honest fail recorded");
            Check.Equal("£2.90", tools.Results["AC1"].Evidence, "evidence is what the page showed");

            browser["#total"] = "£2.70";
            var falseFail = await registry.ExecuteAsync(Call("record_result", new { criterion_id = "AC1", status = "fail", selector = "#total" }));
            Check.Contains("Not recorded: you said fail", falseFail, "false fail rejected");
        });

        suite.Add("story: off-limits navigation and forbidden clicks are blocked before they happen", async () =>
        {
            var browser = new FakeBrowser { ["#pay"] = "Pay now", ["#add"] = "Add to basket" };
            var story = Story(new Criterion("AC1", "x")) with { ForbiddenActions = ["pay"] };
            var registry = new StoryTools(browser, story, NewDirectory()).Create();

            Check.Contains("Blocked by guardrail", await registry.ExecuteAsync(Call("browser_navigate", new { url = "https://elsewhere.example/" })), "navigation");
            Check.Contains("Blocked by guardrail", await registry.ExecuteAsync(Call("browser_click", new { selector = "#pay" })), "forbidden click");
            Check.Contains("Clicked \"Add to basket\"", await registry.ExecuteAsync(Call("browser_click", new { selector = "#add" })), "allowed click");
            Check.Equal(1, browser.Clicks.Count, "only the allowed click reached the browser");
            Check.Equal(0, browser.Navigations.Count, "the blocked navigation never reached the browser");
        });

        suite.Add("story (real browser): a healthy app passes every criterion with evidence", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await RunAsync(repo, "discount-code.json");

            Check.Equal(StoryOutcome.Passed, result.Outcome, "outcome");
            Check.Equal("£2.70", result.Criteria.Single(c => c.Id == "AC2").Evidence, "AC2 evidence");
            Check.True(result.Criteria.All(c => c.Screenshot is not null && new FileInfo(c.Screenshot).Length > 1000), "a screenshot per criterion");
            Check.True(result.Criteria.Single(c => c.Id == "AC3").NeedsReview, "a criterion with no expectation is flagged for review");
        });

        suite.Add("story (real browser): a planted product defect is found and evidenced", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            await repo.GitAsync("apply", "failures/04-wrong-discount.patch");
            var result = await RunAsync(repo, "discount-code.json");

            Check.Equal(StoryOutcome.Failed, result.Outcome, "outcome");
            var ac2 = result.Criteria.Single(c => c.Id == "AC2");
            Check.Equal(CriterionStatus.Failed, ac2.Status, "AC2 status");
            Check.Equal("£2.90", ac2.Evidence, "AC2 evidence is the wrong total the page showed");
            Check.Equal(CriterionStatus.Passed, result.Criteria.Single(c => c.Id == "AC1").Status, "AC1 still passes");
        });

        suite.Add("story (real browser): a tester that claims success without evidence gets 'incomplete', not 'passed'", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            await repo.GitAsync("apply", "failures/04-wrong-discount.patch");
            var result = await RunAsync(repo, "discount-code-overclaims.json");

            Check.Contains("All three acceptance criteria pass", result.Loop.Answer, "the tester claimed everything passed");
            Check.Equal(StoryOutcome.Incomplete, result.Outcome, "outcome");
            Check.Equal(CriterionStatus.Unverified, result.Criteria.Single(c => c.Id == "AC2").Status, "AC2 status");
        });

        suite.Add("story (real browser): straying off site and clicking a forbidden action are blocked, and the job still gets done", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await RunAsync(repo, "discount-code-wanders.json");

            var blocked = result.Loop.ToolEvents.Count(e => e.Result.StartsWith("Blocked by guardrail"));
            Check.Equal(2, blocked, "blocked actions");
            Check.Equal(StoryOutcome.Passed, result.Outcome, "outcome");
        });
    }

    /// <summary>Runs the sample story in a real browser against the app in <paramref name="repo"/>.</summary>
    public static async Task<StoryResult> RunAsync(TempRepo repo, string scriptName, string? outputDirectory = null)
    {
        var sidecar = Path.Combine(TempRepo.SourceRoot, "tools", "browser-sidecar", "sidecar.mjs");
        IBrowser? probe;
        try
        {
            probe = await SidecarBrowser.StartAsync(sidecar);
        }
        catch (Exception error)
        {
            throw new SkipException("No browser available: " + error.Message.Split('\n')[0]);
        }
        await probe.DisposeAsync();

        var story = UserStory.Load(Path.Combine(repo.Path, "sample", "stories", "discount-code.json"), repo.Path);
        var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "sample", "stories", "scripts", scriptName));
        var runner = new StoryRunner(model, async () => await SidecarBrowser.StartAsync(sidecar));
        return await runner.RunAsync(story, outputDirectory ?? NewDirectory());
    }

    private static UserStory Story(params Criterion[] criteria) =>
        new("t", "", "https://shop.example/", ["https://shop.example/"], [], criteria);

    private static ToolCall Call(string name, object arguments) =>
        new("call", name, System.Text.Json.JsonSerializer.Serialize(arguments));

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tah-selftest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A browser made of a dictionary: selector to the text that element shows.</summary>
    private sealed class FakeBrowser : Dictionary<string, string>, IBrowser
    {
        public List<string> Clicks { get; } = [];
        public List<string> Navigations { get; } = [];

        public Task<string> NavigateAsync(string url) { Navigations.Add(url); return Task.FromResult(url); }
        public Task<string> UrlAsync() => Task.FromResult("https://shop.example/");
        public Task<string> SnapshotAsync() => Task.FromResult(string.Join('\n', this.Select(e => $"{e.Key} \"{e.Value}\"")));
        public Task<string> ElementTextAsync(string selector) =>
            TryGetValue(selector, out var text) ? Task.FromResult(text) : throw new InvalidOperationException("No element matches " + selector);
        public Task<string> ClickAsync(string selector) { Clicks.Add(selector); return UrlAsync(); }
        public Task FillAsync(string selector, string value) { this[selector] = value; return Task.CompletedTask; }
        public Task ScreenshotAsync(string path) => File.WriteAllTextAsync(path, "fake");
        public Task<string> PageErrorsAsync() => Task.FromResult("");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
