using FixHarness.Guardrails;
using FixHarness.Testing;

namespace FixHarness.Harness;

/// <summary>
/// The environment one attempt runs in: a throwaway git worktree of the
/// repository. The harness creates it, binds the tools to it and removes it
/// afterwards, whatever happened. The agent never touches your checkout, and a
/// failed attempt leaves nothing behind.
/// </summary>
public sealed class Workspace : IAsyncDisposable
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private readonly string _repoRoot;

    private Workspace(string repoRoot, string path)
    {
        _repoRoot = repoRoot;
        Path = path;
    }

    public string Path { get; }

    /// <param name="plantPatchPath">
    /// Optional patch applied and committed inside the worktree before the agent
    /// starts. Used to plant a known failure without dirtying the repository.
    /// </param>
    public static async Task<Workspace> CreateAsync(string repoPath, string? plantPatchPath = null, string gitRef = "HEAD")
    {
        var repoRoot = (await Git(repoPath, "rev-parse", "--show-toplevel")).Trim();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fixharness-ws-" + Guid.NewGuid().ToString("N")[..10]);

        await Git(repoRoot, "worktree", "add", "--detach", path, gitRef);
        var workspace = new Workspace(repoRoot, System.IO.Path.GetFullPath(path));

        try
        {
            if (plantPatchPath is not null)
            {
                var patch = System.IO.Path.GetFullPath(System.IO.Path.Combine(repoRoot, plantPatchPath));
                await Git(workspace.Path, "apply", patch);
                await Git(workspace.Path, "add", "-A");
                await Git(workspace.Path,
                    "-c", "user.name=fixharness", "-c", "user.email=fixharness@localhost",
                    "commit", "--quiet", "--no-verify", "-m", "fixharness: planted failure");
            }
            return workspace;
        }
        catch
        {
            await workspace.DisposeAsync();
            throw;
        }
    }

    /// <summary>Everything the agent changed, as a patch that applies to the starting commit.</summary>
    public async Task<string> DiffAsync()
    {
        await Git(Path, "add", "-A");
        return await Git(Path, "diff", "--cached", "--no-renames", "HEAD");
    }

    /// <summary>Before and after content of every file the agent changed, for the patch policy.</summary>
    public async Task<Dictionary<string, FileChange>> ChangesAsync()
    {
        await Git(Path, "add", "-A");
        var names = await Git(Path, "diff", "--cached", "--no-renames", "--name-only", "-z", "HEAD");

        var changes = new Dictionary<string, FileChange>();
        foreach (var name in names.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = name.Trim('\n', '\r');
            var show = await ProcessRunner.RunAsync("git", ["show", $"HEAD:{relative}"], Path, GitTimeout);
            var original = show.Succeeded ? show.StdOut : ""; // a new file has no original
            var full = System.IO.Path.Combine(Path, relative);
            var current = File.Exists(full) ? await File.ReadAllTextAsync(full) : "";
            changes[relative] = new FileChange(Normalise(original), Normalise(current));
        }
        return changes;
    }

    public async ValueTask DisposeAsync()
    {
        await ProcessRunner.RunAsync("git", ["worktree", "remove", "--force", Path], _repoRoot, GitTimeout);
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best effort: a locked file should not hide the real result.
        }
        await ProcessRunner.RunAsync("git", ["worktree", "prune"], _repoRoot, GitTimeout);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n");

    private static async Task<string> Git(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeout);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Output.Trim()}");
        }
        return result.StdOut;
    }
}
