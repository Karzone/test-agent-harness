using System.Text.Json;

namespace TestAgentHarness.Story;

/// <param name="Expect">
/// Optional text the page must show for this criterion to pass (for example "£2.70").
/// When given, the harness checks it itself; the model cannot pass the criterion without it.
/// </param>
public sealed record Criterion(string Id, string Text, string? Expect = null);

/// <summary>A user story to check by hand in a browser: where to start, what must be true, what is off limits.</summary>
public sealed record UserStory(
    string Title,
    string Narrative,
    string StartUrl,
    IReadOnlyList<string> AllowedUrls,
    IReadOnlyList<string> ForbiddenActions,
    IReadOnlyList<Criterion> Criteria)
{
    /// <summary>Things no story run may click unless the story file says otherwise.</summary>
    public static readonly string[] DefaultForbiddenActions = ["delete", "remove account", "pay", "purchase", "place order", "unsubscribe"];

    /// <summary>
    /// Loads a story from JSON. A startUrl that is not http(s) is treated as a file
    /// path relative to <paramref name="baseDirectory"/>.
    /// </summary>
    public static UserStory Load(string path, string baseDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var start = root.GetProperty("startUrl").GetString()!;
        if (!start.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            start = new Uri(Path.GetFullPath(Path.Combine(baseDirectory, start))).AbsoluteUri;
        }

        var allowed = Strings(root, "allowedUrls");
        if (allowed.Count == 0)
        {
            // Default: stay on the site (or, for a file, in the folder) the story starts from.
            var uri = new Uri(start);
            allowed = [uri.IsFile ? start[..(start.LastIndexOf('/') + 1)] : uri.GetLeftPart(UriPartial.Authority) + "/"];
        }

        IReadOnlyList<string> forbidden = root.TryGetProperty("forbiddenActions", out _) ? Strings(root, "forbiddenActions") : DefaultForbiddenActions;

        var criteria = root.GetProperty("criteria").EnumerateArray().Select(c => new Criterion(
            c.GetProperty("id").GetString()!,
            c.GetProperty("text").GetString()!,
            c.TryGetProperty("expect", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)).ToList();

        return new UserStory(
            root.GetProperty("title").GetString()!,
            root.TryGetProperty("story", out var s) ? s.GetString() ?? "" : "",
            start,
            allowed,
            forbidden,
            criteria);
    }

    public bool IsAllowed(string url) => AllowedUrls.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static List<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];
}

public enum CriterionStatus
{
    Passed,
    Failed,
    /// <summary>No result backed by evidence the harness read itself. Never counted as a pass.</summary>
    Unverified,
}

/// <param name="Evidence">The text of the element, read by the harness at the moment the result was recorded.</param>
/// <param name="NeedsReview">True when the criterion has no checkable expectation, so a person should confirm the evidence is enough.</param>
public sealed record CriterionResult(
    string Id,
    string Text,
    CriterionStatus Status,
    string Evidence,
    string? Selector = null,
    string? Url = null,
    string? Screenshot = null,
    string? Note = null,
    bool NeedsReview = false);
