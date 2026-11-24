namespace TestAgentHarness.Testing;

/// <summary>
/// Runs "dotnet test" on one project with a filter and reads the results from
/// the .trx file. Works with any framework that dotnet test supports
/// (NUnit, xUnit, MSTest, and Reqnroll on top of them).
/// </summary>
public sealed class DotnetTestRunner(string testProjectRelativePath, TimeSpan? timeout = null) : ITestRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    // Build servers that outlive the command keep files open in the throwaway
    // workspace and stop it being deleted, so they are switched off.
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["DOTNET_NOLOGO"] = "1",
    };

    public async Task<TestRunResult> RunAsync(string workingDirectory, string filter, CancellationToken cancellationToken = default)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "tah-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDirectory);

        try
        {
            var process = await ProcessRunner.RunAsync(
                "dotnet",
                [
                    "test", testProjectRelativePath,
                    "--filter", filter,
                    "--logger", "trx;LogFileName=results.trx",
                    "--results-directory", resultsDirectory,
                    "--nologo",
                ],
                workingDirectory,
                _timeout,
                cancellationToken,
                Environment);

            var tail = Tail(process.Output, 2500);
            if (process.TimedOut)
            {
                return new TestRunResult(false, [], $"Timed out after {_timeout.TotalMinutes:0} minute(s).\n{tail}");
            }

            var trxPath = Path.Combine(resultsDirectory, "results.trx");
            if (File.Exists(trxPath))
            {
                return new TestRunResult(true, TrxParser.Parse(await File.ReadAllTextAsync(trxPath, cancellationToken)), tail);
            }

            // No results file: either the filter matched nothing (exit code 0),
            // or the build or the test host failed before any test ran.
            return new TestRunResult(BuildSucceeded: process.ExitCode == 0, [], tail);
        }
        finally
        {
            try { Directory.Delete(resultsDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "..." + text[^max..];
}
