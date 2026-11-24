using TestAgentHarness.Testing;

namespace TestAgentHarness.SelfTests;

/// <summary>A minimal test runner: named async tests, a pass/fail line each, a non-zero exit code on failure.</summary>
public sealed class Suite
{
    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));
    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public async Task<int> RunAsync(string? only)
    {
        var failed = 0;
        foreach (var (name, body) in _tests.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await body();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (SkipException skip)
            {
                Console.WriteLine($"  SKIP  {name}\n        {skip.Message}");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n        {error.Message.ReplaceLineEndings("\n        ")}");
            }
        }

        Console.WriteLine(failed == 0 ? "\nAll self-tests passed." : $"\n{failed} self-test(s) FAILED.");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>Thrown by a test that cannot run in this environment (for example, no browser).</summary>
public sealed class SkipException(string reason) : Exception(reason);

public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception("Expected: " + what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected <{expected}> but was <{actual}>");
        }
    }

    public static void Contains(string needle, string haystack, string what)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"{what}: expected to find \"{needle}\" in:\n{haystack}");
        }
    }
}

/// <summary>A real, temporary git repository holding a copy of this repo's sample app, tests and planted failures.</summary>
public sealed class TempRepo : IDisposable
{
    private TempRepo(string path) => Path = path;

    public string Path { get; }

    public static string SourceRoot { get; } = FindSourceRoot();

    public static async Task<TempRepo> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tah-selftest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);

        CopyDirectory(System.IO.Path.Combine(SourceRoot, "sample"), System.IO.Path.Combine(path, "sample"));
        CopyDirectory(System.IO.Path.Combine(SourceRoot, "failures"), System.IO.Path.Combine(path, "failures"));
        File.Copy(System.IO.Path.Combine(SourceRoot, "testagentharness.json"), System.IO.Path.Combine(path, "testagentharness.json"));
        File.Copy(System.IO.Path.Combine(SourceRoot, ".gitignore"), System.IO.Path.Combine(path, ".gitignore"));

        var repo = new TempRepo(path);
        await repo.GitAsync("init", "--quiet", "-b", "main");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("-c", "user.name=selftest", "-c", "user.email=selftest@localhost", "commit", "--quiet", "-m", "sample");
        return repo;
    }

    public async Task<string> GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, Path, TimeSpan.FromSeconds(60));
        if (!result.Succeeded) throw new Exception($"git {string.Join(' ', arguments)} failed: {result.Output}");
        return result.StdOut;
    }

    public string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only, which blocks deletion on Windows.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Leaving a temp directory behind is not worth failing a test for.
        }
    }

    private static string FindSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "TestAgentHarness.sln"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not find TestAgentHarness.sln above " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = System.IO.Path.GetFileName(directory);
            if (name is "bin" or "obj" or "TestResults") continue;
            CopyDirectory(directory, System.IO.Path.Combine(destination, name));
        }
    }
}
