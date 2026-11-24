using System.Text.RegularExpressions;
using FixHarness.Testing;

namespace FixHarness.Harness;

/// <summary>
/// Finds out what changed before the test started failing. This is the best
/// evidence there is: it feeds the triage rules and it is shown to the model,
/// so the model does not have to go looking for what moved.
/// </summary>
public static partial class GitChanges
{
    /// <summary>The files a unified diff touches.</summary>
    public static IReadOnlyList<string> PathsInPatch(string patchText) =>
        PatchPathRegex().Matches(patchText).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    /// <summary>The diff between a base ref (for example the target branch) and HEAD.</summary>
    public static async Task<string> DiffSinceAsync(string repoPath, string baseRef)
    {
        var result = await ProcessRunner.RunAsync("git", ["diff", "--no-renames", $"{baseRef}...HEAD"], repoPath, TimeSpan.FromSeconds(60));
        if (!result.Succeeded) throw new InvalidOperationException($"Could not diff against {baseRef}: {result.Output.Trim()}");
        return result.StdOut;
    }

    [GeneratedRegex(@"^\+\+\+ b/(.+)$", RegexOptions.Multiline)]
    private static partial Regex PatchPathRegex();
}
