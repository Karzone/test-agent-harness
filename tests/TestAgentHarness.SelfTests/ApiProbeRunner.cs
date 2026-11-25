using TestAgentHarness.Testing;

namespace TestAgentHarness.SelfTests;

/// <summary>
/// Runs the API tests for real, through tests/ApiProbe: it compiles the test
/// client from the workspace, hosts the real API in-process and makes real HTTP
/// calls. Unlike the UI simulator, nothing here is worked out from reading
/// files. The only stand-in is that the test methods are mirrored in the probe,
/// because the NUnit packages are not available everywhere.
/// </summary>
public sealed class ApiProbeRunner : ITestRunner
{
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
    };

    public int Runs { get; private set; }

    public async Task<TestRunResult> RunAsync(string workingDirectory, string filter, CancellationToken cancellationToken = default)
    {
        Runs++;
        const string prefix = "FullyQualifiedName~";
        var only = filter.StartsWith(prefix, StringComparison.Ordinal) ? filter[prefix.Length..] : filter;

        var process = await ProcessRunner.RunAsync(
            "dotnet", ["run", "--project", "tests/ApiProbe", "--", only],
            workingDirectory, TimeSpan.FromMinutes(5), cancellationToken, Environment);

        var tests = new List<TestCaseResult>();
        foreach (var line in process.StdOut.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var parts = line.Split('|', 3);
            if (parts[0] == "PASS" && parts.Length >= 2)
            {
                tests.Add(new TestCaseResult(parts[1], TestOutcome.Passed));
            }
            else if (parts[0] == "FAIL" && parts.Length == 3)
            {
                tests.Add(new TestCaseResult(parts[1], TestOutcome.Failed, parts[2].Replace("\\n", "\n")));
            }
        }

        // No result lines and a failing exit code means the probe did not build or start.
        var built = tests.Count > 0 || process.Succeeded;
        var tail = process.Output.Length <= 2500 ? process.Output : "..." + process.Output[^2500..];
        return new TestRunResult(built, tests, tail);
    }
}
