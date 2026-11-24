using System.Text.Json;
using System.Text.Json.Serialization;
using FixHarness.Triage;

namespace FixHarness.Memory;

public enum MemoryKind
{
    /// <summary>A patch the verifier accepted. Can be replayed and used as a hint.</summary>
    Fix,
    /// <summary>An approach the verifier rejected. Used as a "do not repeat this" hint.</summary>
    Rejection,
    /// <summary>A person's ruling on what kind of failure this is. Outranks every rule and the model.</summary>
    Label,
}

public sealed record MemoryEntry(
    string Id,
    MemoryKind Kind,
    DateTime CreatedUtc,
    string Filter,
    string ExactKey,
    string Shape,
    string Headline,
    string Lesson,
    string? Patch = null,
    IReadOnlyList<string>? Files = null,
    FailureKind? Label = null,
    int Uses = 0,
    int Failures = 0);

/// <summary>
/// What the harness has learned in this repository, as a JSON-lines file you
/// can read, review in a pull request and edit by hand.
///
/// Three rules keep it trustworthy:
///   1. It only learns from evidence: fixes the verifier accepted, attempts the
///      verifier rejected, and labels a person gave. A model's opinion is never stored.
///   2. A remembered fix is never trusted on sight. Replaying it means applying
///      the patch and running the full verifier again.
///   3. It corrects itself. A remembered fix that fails verification twice is
///      no longer replayed.
/// </summary>
public sealed class FixMemory
{
    private const double HintSimilarity = 0.6;
    private const int MaxReplayFailures = 2;
    private const int MaxPatchChars = 8_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly List<MemoryEntry> _entries = [];

    public FixMemory(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                if (JsonSerializer.Deserialize<MemoryEntry>(line, JsonOptions) is { } entry) _entries.Add(entry);
            }
            catch (JsonException)
            {
                // A hand-edited line that no longer parses is skipped, not fatal.
            }
        }
    }

    public IReadOnlyList<MemoryEntry> Entries => _entries;

    /// <summary>A person's ruling on exactly this failure, if there is one. The newest wins.</summary>
    public MemoryEntry? HumanLabel(FailureSignature signature) =>
        _entries.LastOrDefault(e => e.Kind == MemoryKind.Label && e.ExactKey == signature.ExactKey);

    /// <summary>A verified fix for exactly this failure that is still trusted.</summary>
    public MemoryEntry? FindReplay(FailureSignature signature) =>
        _entries.LastOrDefault(e =>
            e.Kind == MemoryKind.Fix
            && e.ExactKey == signature.ExactKey
            && e.Patch is not null
            && e.Failures < MaxReplayFailures);

    /// <summary>Short hints from similar past failures, most similar first.</summary>
    public IReadOnlyList<string> Lessons(FailureSignature signature, int max = 3) =>
        _entries
            .Select(entry => (entry, score: entry.ExactKey == signature.ExactKey ? 1.0 : FailureSignature.Similarity(entry.Shape, signature.Shape)))
            .Where(x => x.score >= HintSimilarity)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.entry.CreatedUtc)
            .Select(x => Describe(x.entry))
            .Distinct()
            .Take(max)
            .ToList();

    public void RecordFix(FailureSignature signature, string filter, string lesson, string patch, IReadOnlyList<string> files)
    {
        _entries.RemoveAll(e => e.Kind == MemoryKind.Fix && e.ExactKey == signature.ExactKey && e.Patch == patch);
        Add(signature, MemoryKind.Fix, filter, lesson, e => e with { Patch = patch.Length <= MaxPatchChars ? patch : null, Files = files });
    }

    public void RecordRejection(FailureSignature signature, string filter, string reason)
    {
        var lesson = Shorten(reason, 300);
        if (_entries.Any(e => e.Kind == MemoryKind.Rejection && e.ExactKey == signature.ExactKey && e.Lesson == lesson)) return;
        Add(signature, MemoryKind.Rejection, filter, lesson);
    }

    public void RecordLabel(FailureSignature signature, string filter, FailureKind label, string? note)
    {
        Add(signature, MemoryKind.Label, filter, note ?? "", e => e with { Label = label });
    }

    /// <summary>Records how a replay went, so a fix that stops working stops being replayed.</summary>
    public void RecordReplay(MemoryEntry entry, bool succeeded)
    {
        var index = _entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) return;
        _entries[index] = succeeded
            ? entry with { Uses = entry.Uses + 1, Failures = 0 }
            : entry with { Failures = entry.Failures + 1 };
        Save();
    }

    private void Add(FailureSignature signature, MemoryKind kind, string filter, string lesson, Func<MemoryEntry, MemoryEntry>? customise = null)
    {
        var entry = new MemoryEntry(
            Guid.NewGuid().ToString("N")[..12], kind, DateTime.UtcNow, filter,
            signature.ExactKey, signature.Shape, signature.Headline, Shorten(lesson, 400));
        _entries.Add(customise?.Invoke(entry) ?? entry);
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllLines(temporary, _entries.Select(e => JsonSerializer.Serialize(e, JsonOptions)));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string Describe(MemoryEntry entry) => entry.Kind switch
    {
        MemoryKind.Fix => $"A failure like this (\"{entry.Headline}\") was fixed and verified by changing {string.Join(", ", entry.Files ?? [])}. {entry.Lesson}",
        MemoryKind.Rejection => $"For a failure like this (\"{entry.Headline}\"), this attempt was rejected: {entry.Lesson}",
        _ => $"A person labelled a failure like this (\"{entry.Headline}\") as a {(entry.Label == FailureKind.ProductDefect ? "product defect" : "test issue")}. {entry.Lesson}",
    };

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max] + "...";
    }
}
