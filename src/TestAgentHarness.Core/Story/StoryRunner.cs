using System.Text;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Loop;
using TestAgentHarness.Model;

namespace TestAgentHarness.Story;

public enum StoryOutcome
{
    /// <summary>Every criterion passed, each backed by evidence the harness read.</summary>
    Passed,
    /// <summary>At least one criterion failed, with evidence.</summary>
    Failed,
    /// <summary>Nothing failed, but at least one criterion has no verified result. Not a pass.</summary>
    Incomplete,
}

public sealed record StoryResult(
    StoryOutcome Outcome,
    string Title,
    string Model,
    IReadOnlyList<CriterionResult> Criteria,
    LoopResult Loop,
    string PageErrors,
    TimeSpan Duration)
{
    public int ModelCalls => Loop.Iterations;
}

/// <summary>
/// Checks a user story in a real browser, the way a tester would by hand.
/// The same harness shape as the fixer, with a browser as the environment:
///
///   open a browser -> go to the story's start page -> agent loop with browser
///   tools and guardrails -> collect per-criterion results -> close the browser
///
/// A criterion only counts as passed or failed if the harness itself read the
/// evidence from the page. Whatever the model says in its final answer, a
/// criterion with no recorded evidence is reported as unverified.
/// </summary>
public sealed class StoryRunner(IModelClient model, Func<Task<IBrowser>> openBrowser, Action<string>? log = null)
{
    private const string SystemPrompt = """
        You are checking a user story by hand in a web browser, as a careful tester would.

        How to work:
        - Use browser_snapshot to see the page and get selectors. Act with browser_click and browser_fill.
        - Check each acceptance criterion by actually doing what it describes.
        - For each criterion call record_result with "pass" or "fail" and the selector of the element that shows the outcome. The harness reads that element itself and takes a screenshot, so record what the page really shows.
        - A criterion that fails is a useful finding. Record it as "fail"; do not try to make it pass.
        - Some URLs and actions are off limits; a blocked call tells you so. Do not try to get round it.
        - When every criterion has a recorded result, reply in plain text with a one-paragraph summary. Do not call a tool in that final reply.
        """;

    public async Task<StoryResult> RunAsync(UserStory story, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var browser = await openBrowser();
        log?.Invoke($"[harness] browser open, going to {story.StartUrl}");
        await browser.NavigateAsync(story.StartUrl);

        var storyTools = new StoryTools(browser, story, Path.Combine(outputDirectory, "screenshots"));
        var messages = new List<ChatMessage> { ChatMessage.System(SystemPrompt), ChatMessage.User(Describe(story)) };

        // Stop as soon as every criterion has evidence: nothing more to learn, and no chance to wander.
        var guardrail = LoopGuardrails.Combine(
            _ => storyTools.AllRecorded ? "All acceptance criteria have a recorded result." : null,
            LoopGuardrails.MaxIterations(30),
            LoopGuardrails.TimeBudget(TimeSpan.FromMinutes(10)),
            LoopGuardrails.NotStuck());

        var loop = await AgentLoop.RunAsync(model, messages, storyTools.Create(), guardrail, log, cancellationToken);

        var results = story.Criteria
            .Select(c => storyTools.Results.GetValueOrDefault(c.Id) ?? new CriterionResult(
                c.Id, c.Text, CriterionStatus.Unverified,
                "No result backed by evidence from the page was recorded for this criterion."))
            .ToList();

        var outcome = results.Any(r => r.Status == CriterionStatus.Failed) ? StoryOutcome.Failed
            : results.Any(r => r.Status == CriterionStatus.Unverified) ? StoryOutcome.Incomplete
            : StoryOutcome.Passed;

        var pageErrors = await browser.PageErrorsAsync();
        log?.Invoke($"[harness] story outcome: {outcome}");
        return new StoryResult(outcome, story.Title, model.Name, results, loop, pageErrors, clock.Elapsed);
    } // the browser is closed here, even if anything above threw

    private static string Describe(UserStory story)
    {
        var text = new StringBuilder();
        text.AppendLine($"Story: {story.Title}");
        if (story.Narrative.Length > 0) text.AppendLine(story.Narrative);
        text.AppendLine();
        text.AppendLine($"The browser is already open at: {story.StartUrl}");
        text.AppendLine();
        text.AppendLine("Acceptance criteria:");
        foreach (var criterion in story.Criteria) text.AppendLine($"- {criterion.Id}: {criterion.Text}");
        return text.ToString().TrimEnd();
    }

    public static string ToMarkdown(StoryResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"## Story check: {result.Outcome}");
        text.AppendLine();
        text.AppendLine($"**{result.Title}** (model: {result.Model}, {result.ModelCalls} call(s), {result.Duration.TotalSeconds:0.0} s)");
        text.AppendLine();
        text.AppendLine("| Criterion | Result | Evidence read from the page | Element |");
        text.AppendLine("|---|---|---|---|");
        foreach (var c in result.Criteria)
        {
            var review = c.NeedsReview ? " (no checkable expectation: review the evidence)" : "";
            text.AppendLine($"| {c.Id}: {c.Text} | {c.Status}{review} | {c.Evidence.ReplaceLineEndings(" ")} | {(c.Selector is null ? "" : $"`{c.Selector}`")} |");
        }

        if (result.PageErrors.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("### Script errors thrown by the page");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(result.PageErrors);
            text.AppendLine("```");
        }

        var blocked = result.Loop.ToolEvents.Where(e => e.Result.StartsWith("Blocked by guardrail", StringComparison.Ordinal)).ToList();
        if (blocked.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("### Actions the guardrails blocked");
            text.AppendLine();
            foreach (var e in blocked) text.AppendLine($"- {e.Result}");
        }

        return text.ToString();
    }
}
