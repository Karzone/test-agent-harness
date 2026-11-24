using System.Diagnostics;
using System.Text;

namespace FixHarness.Testing;

/// <param name="StdOut">Standard output only, byte-for-byte (used for patches and file content).</param>
/// <param name="Output">Standard output followed by standard error (used for messages).</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string Output, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs a command without a shell, captures its output and enforces a timeout.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read both streams concurrently so a full pipe can never block the child.
        var stdOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var output = await stdOut;
        var error = await stdErr;
        return new ProcessResult(timedOut ? -1 : process.ExitCode, output, output + error, timedOut);
    }
}
