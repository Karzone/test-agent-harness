using System.Text;
using System.Text.Json;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;

namespace TestAgentHarness.Tools;

/// <summary>
/// The tools the model gets. They are bound to one working directory and one
/// failing test, both chosen by the harness. The model cannot point them
/// anywhere else.
/// </summary>
public static class WorkspaceTools
{
    private const int MaxFileChars = 12_000;
    private const int MaxListedFiles = 200;
    private const int MaxSearchHits = 40;

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", "TestResults", ".vs"];
    private static readonly string[] TextExtensions = [".cs", ".feature", ".html", ".js", ".ts", ".json", ".css", ".md", ".csproj", ".config", ".xml", ".txt"];

    /// <param name="policy">
    /// When given, every edit is checked against the policy before it is written.
    /// A rejected edit is not applied, and the model is told why.
    /// </param>
    public static ToolRegistry Create(string workingDirectory, ITestRunner testRunner, string testFilter, PatchPolicy? policy = null)
    {
        var root = Path.GetFullPath(workingDirectory);

        // Content of each file before the agent first touched it, so the policy
        // judges the whole fix so far and not just the latest edit.
        var originals = new Dictionary<string, string>();

        return new ToolRegistry(
        [
            new Tool(
                new ToolDefinition(
                    "run_test",
                    "Build the test project and run the failing test. Returns pass or fail with the error message and stack trace.",
                    """{"type":"object","properties":{},"required":[]}"""),
                async (_, cancellationToken) =>
                    (await testRunner.RunAsync(root, testFilter, cancellationToken)).Describe()),

            new Tool(
                new ToolDefinition(
                    "list_files",
                    "List source files under a directory (relative path; empty for the whole repository).",
                    """{"type":"object","properties":{"directory":{"type":"string"}},"required":[]}"""),
                (arguments, _) => Task.FromResult(ListFiles(root, OptionalString(arguments, "directory") ?? ""))),

            new Tool(
                new ToolDefinition(
                    "read_file",
                    "Read a text file by relative path.",
                    """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
                (arguments, _) => Task.FromResult(ReadFile(root, RequiredString(arguments, "path")))),

            new Tool(
                new ToolDefinition(
                    "search",
                    "Find lines containing some text (case-insensitive) across the source files. Returns path:line: text.",
                    """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
                (arguments, _) => Task.FromResult(Search(root, RequiredString(arguments, "text")))),

            new Tool(
                new ToolDefinition(
                    "replace_in_file",
                    "Edit a file by replacing one exact piece of text with another. old_text must appear exactly once in the file, so include enough surrounding text to make it unique.",
                    """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}"""),
                (arguments, _) => Task.FromResult(ReplaceInFile(
                    root,
                    policy,
                    originals,
                    RequiredString(arguments, "path"),
                    RequiredString(arguments, "old_text"),
                    RequiredString(arguments, "new_text")))),
        ]);
    }

    private static string ListFiles(string root, string directory)
    {
        var start = Resolve(root, directory);
        if (!Directory.Exists(start)) return $"Directory not found: {directory}";

        var files = EnumerateSourceFiles(start).Select(f => Relative(root, f)).Order().ToList();
        var listing = string.Join('\n', files.Take(MaxListedFiles));
        return files.Count > MaxListedFiles ? listing + $"\n...({files.Count - MaxListedFiles} more)" : listing;
    }

    private static string ReadFile(string root, string path)
    {
        var full = Resolve(root, path);
        if (!File.Exists(full)) return $"File not found: {path}";

        var text = File.ReadAllText(full);
        return text.Length <= MaxFileChars ? text : text[..MaxFileChars] + "\n...(truncated)";
    }

    private static string Search(string root, string text)
    {
        var hits = new List<string>();
        foreach (var file in EnumerateSourceFiles(root))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add($"{Relative(root, file)}:{lineNumber}: {line.Trim()}");
                if (hits.Count >= MaxSearchHits) return string.Join('\n', hits) + "\n...(more matches not shown)";
            }
        }

        return hits.Count == 0 ? $"No matches for \"{text}\"." : string.Join('\n', hits);
    }

    private static string ReplaceInFile(
        string root,
        PatchPolicy? policy,
        Dictionary<string, string> originals,
        string path,
        string oldText,
        string newText)
    {
        var full = Resolve(root, path);
        if (!File.Exists(full)) return $"File not found: {path}";

        var original = File.ReadAllText(full);
        var usesCrLf = original.Contains("\r\n");

        // Match on normalised line endings so the model does not have to guess them.
        var before = original.Replace("\r\n", "\n");
        var find = oldText.Replace("\r\n", "\n");
        var replacement = newText.Replace("\r\n", "\n");

        if (find.Length == 0) return "Rejected: old_text is empty.";
        var occurrences = CountOccurrences(before, find);
        if (occurrences == 0) return "Rejected: old_text was not found in the file. Read the file and copy the text exactly.";
        if (occurrences > 1) return $"Rejected: old_text appears {occurrences} times. Include more surrounding text so it is unique.";

        var index = before.IndexOf(find, StringComparison.Ordinal);
        var after = string.Concat(before.AsSpan(0, index), replacement, before.AsSpan(index + find.Length));

        var relative = Relative(root, full);
        if (policy is not null)
        {
            var violations = policy.Check(ProposedChanges(root, originals, relative, before, after));
            if (violations.Count > 0)
            {
                return "Rejected by guardrail, nothing was changed:\n- " + string.Join("\n- ", violations);
            }
        }

        originals.TryAdd(relative, before);
        File.WriteAllText(full, usesCrLf ? after.Replace("\n", "\r\n") : after, new UTF8Encoding(false));
        return $"Edited {relative}.";
    }

    /// <summary>Every file changed so far, plus the edit being proposed.</summary>
    private static Dictionary<string, FileChange> ProposedChanges(
        string root,
        Dictionary<string, string> originals,
        string editedPath,
        string editedBefore,
        string editedAfter)
    {
        var changes = new Dictionary<string, FileChange>();
        foreach (var (path, original) in originals)
        {
            if (path == editedPath) continue;
            var current = File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");
            if (current != original) changes[path] = new FileChange(original, current);
        }

        var editedOriginal = originals.GetValueOrDefault(editedPath, editedBefore);
        if (editedAfter != editedOriginal) changes[editedPath] = new FileChange(editedOriginal, editedAfter);
        return changes;
    }

    /// <summary>Turns a relative path into a full one and refuses anything outside the workspace.</summary>
    public static string Resolve(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var inside = full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!inside) throw new InvalidOperationException($"Path is outside the workspace: {relativePath}");
        return full;
    }

    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child))) continue;
            foreach (var file in EnumerateSourceFiles(child)) yield return file;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ArgumentException($"Missing required argument \"{name}\".");

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
