using System.Text.Json;
using TestAgentHarness.Model;

namespace TestAgentHarness.Cli;

/// <summary>Command-line options: "--name value" pairs and bare "--flag" switches.</summary>
public sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public CliOptions(IEnumerable<string> arguments)
    {
        string? pending = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) _values[pending] = "true";
                pending = argument[2..];
            }
            else if (pending is not null)
            {
                _values[pending] = argument;
                pending = null;
            }
            else
            {
                throw new ArgumentException($"Unexpected argument: {argument}");
            }
        }
        if (pending is not null) _values[pending] = "true";
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback) => int.TryParse(Get(name), out var value) ? value : fallback;
}

/// <summary>Repository settings from testagentharness.json at the repository root.</summary>
public sealed record RepoConfig(string TestProject, string[] TestRoots, string? NeighbourFilter)
{
    public static RepoConfig Load(string repoPath)
    {
        var path = Path.Combine(repoPath, "testagentharness.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"No testagentharness.json found in {repoPath}.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new RepoConfig(
            root.GetProperty("testProject").GetString()!,
            root.GetProperty("testRoots").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            root.TryGetProperty("neighbourFilter", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
    }
}

/// <summary>One entry in failures/manifest.json.</summary>
public sealed record PlantedFailure(string Id, string Kind, string Title, string Patch, string Filter, string PrNote, string ExpectedOutcome)
{
    public static IReadOnlyList<PlantedFailure> LoadAll(string repoPath)
    {
        var path = Path.Combine(repoPath, "failures", "manifest.json");
        if (!File.Exists(path)) return [];

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Select(e => new PlantedFailure(
            e.GetProperty("id").GetString()!,
            e.GetProperty("kind").GetString()!,
            e.GetProperty("title").GetString()!,
            e.GetProperty("patch").GetString()!,
            e.GetProperty("filter").GetString()!,
            e.GetProperty("prNote").GetString()!,
            e.GetProperty("expectedOutcome").GetString()!)).ToList();
    }
}

public static class ModelFactory
{
    /// <summary>
    /// "scripted:path" replays a script and needs no API key; anything else is a
    /// model name for the API. The path may be a script file, or a directory
    /// holding one script per planted failure (&lt;failure id&gt;.json).
    /// </summary>
    public static IModelClient Create(string? modelOption, string repoPath, string? failureId = null)
    {
        const string scriptedPrefix = "scripted:";
        if (modelOption is null || !modelOption.StartsWith(scriptedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiCompatibleClient.FromEnvironment(modelOption);
        }

        var path = Path.GetFullPath(Path.Combine(repoPath, modelOption[scriptedPrefix.Length..]));
        if (Directory.Exists(path)) path = Path.Combine(path, (failureId ?? "default") + ".json");

        // No script for this failure: a "model" that answers immediately and changes nothing.
        return File.Exists(path) ? ScriptedModelClient.FromFile(path) : new ScriptedModelClient([]);
    }
}
