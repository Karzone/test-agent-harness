using System.Text.Json;
using TestAgentHarness.Model;
using TestAgentHarness.Tools;

namespace TestAgentHarness.Story;

/// <summary>
/// The tools a model gets for checking a story, bound to one browser and one
/// story. Two things are enforced here in plain code, not left to the model:
///
///   Guardrails: it can only visit allowed URLs and cannot click forbidden actions.
///   Evidence:   it cannot simply declare a criterion passed. It points at an
///               element; the harness reads that element itself, checks the
///               expectation, and takes the screenshot.
/// </summary>
public sealed class StoryTools(IBrowser browser, UserStory story, string screenshotDirectory)
{
    private readonly Dictionary<string, CriterionResult> _results = [];

    public IReadOnlyDictionary<string, CriterionResult> Results => _results;

    public bool AllRecorded => story.Criteria.All(c => _results.ContainsKey(c.Id));

    public ToolRegistry Create() => new(
    [
        new Tool(
            new ToolDefinition("browser_snapshot",
                "See the current page: its visible text and the elements you can act on, each with a selector.",
                """{"type":"object","properties":{},"required":[]}"""),
            async (_, _) => await browser.SnapshotAsync()),

        new Tool(
            new ToolDefinition("browser_navigate",
                "Go to a URL. Only URLs within the story's allowed area can be opened.",
                """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
            async (arguments, _) => await NavigateAsync(Text(arguments, "url"))),

        new Tool(
            new ToolDefinition("browser_click",
                "Click an element, using a selector from browser_snapshot.",
                """{"type":"object","properties":{"selector":{"type":"string"}},"required":["selector"]}"""),
            async (arguments, _) => await ClickAsync(Text(arguments, "selector"))),

        new Tool(
            new ToolDefinition("browser_fill",
                "Type a value into an input, using a selector from browser_snapshot.",
                """{"type":"object","properties":{"selector":{"type":"string"},"value":{"type":"string"}},"required":["selector","value"]}"""),
            async (arguments, _) =>
            {
                await browser.FillAsync(Text(arguments, "selector"), Text(arguments, "value"));
                return "Filled.";
            }),

        new Tool(
            new ToolDefinition("record_result",
                "Record the result for one acceptance criterion. Give the selector of the element that shows the outcome; the harness reads that element itself and takes a screenshot. status is \"pass\" or \"fail\".",
                """{"type":"object","properties":{"criterion_id":{"type":"string"},"status":{"type":"string","enum":["pass","fail"]},"selector":{"type":"string"},"note":{"type":"string"}},"required":["criterion_id","status","selector"]}"""),
            async (arguments, _) => await RecordAsync(
                Text(arguments, "criterion_id"),
                Text(arguments, "status"),
                Text(arguments, "selector"),
                arguments.TryGetProperty("note", out var note) ? note.GetString() : null)),
    ]);

    private async Task<string> NavigateAsync(string url)
    {
        if (!story.IsAllowed(url))
        {
            return $"Blocked by guardrail: {url} is outside the area this story may visit ({string.Join(", ", story.AllowedUrls)}).";
        }
        return "Now at " + await browser.NavigateAsync(url);
    }

    private async Task<string> ClickAsync(string selector)
    {
        var label = await browser.ElementTextAsync(selector);
        var forbidden = story.ForbiddenActions.FirstOrDefault(action => label.Contains(action, StringComparison.OrdinalIgnoreCase));
        if (forbidden is not null)
        {
            return $"Blocked by guardrail: \"{label}\" is a forbidden action for this story (\"{forbidden}\"). Nothing was clicked.";
        }

        var before = await browser.UrlAsync();
        var after = await browser.ClickAsync(selector);
        if (!story.IsAllowed(after))
        {
            await browser.NavigateAsync(before);
            return $"Blocked by guardrail: the click led to {after}, outside the allowed area. Returned to {before}.";
        }
        return $"Clicked \"{label}\". Now at {after}.";
    }

    private async Task<string> RecordAsync(string criterionId, string status, string selector, string? note)
    {
        var criterion = story.Criteria.FirstOrDefault(c => c.Id.Equals(criterionId, StringComparison.OrdinalIgnoreCase));
        if (criterion is null) return $"Unknown criterion \"{criterionId}\". Criteria: {string.Join(", ", story.Criteria.Select(c => c.Id))}.";
        if (status is not ("pass" or "fail")) return "status must be \"pass\" or \"fail\".";

        // The harness reads the evidence. What the model believes the element says is irrelevant.
        string observed;
        try
        {
            observed = (await browser.ElementTextAsync(selector)).Trim();
        }
        catch (Exception error)
        {
            return $"Not recorded: could not read {selector} ({error.Message}). Use a selector from browser_snapshot.";
        }

        var passing = status == "pass";
        if (criterion.Expect is { } expected)
        {
            var shown = observed.Contains(expected, StringComparison.Ordinal);
            if (passing && !shown)
            {
                return $"Not recorded: you said pass, but {selector} shows \"{observed}\", not the expected \"{expected}\". Record what the page really shows.";
            }
            if (!passing && shown)
            {
                return $"Not recorded: you said fail, but {selector} shows the expected \"{expected}\".";
            }
        }
        else if (observed.Length == 0)
        {
            return $"Not recorded: {selector} has no visible text, so it is not evidence of anything.";
        }

        Directory.CreateDirectory(screenshotDirectory);
        var screenshot = Path.Combine(screenshotDirectory, criterion.Id + ".png");
        await browser.ScreenshotAsync(screenshot);

        _results[criterion.Id] = new CriterionResult(
            criterion.Id,
            criterion.Text,
            passing ? CriterionStatus.Passed : CriterionStatus.Failed,
            observed,
            selector,
            await browser.UrlAsync(),
            screenshot,
            note,
            NeedsReview: criterion.Expect is null);

        return $"Recorded {criterion.Id} as {status}. Evidence read from the page: \"{observed}\".";
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"Missing required argument \"{name}\".");
}
