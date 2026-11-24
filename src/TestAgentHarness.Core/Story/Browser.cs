using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TestAgentHarness.Story;

/// <summary>
/// The browser the harness drives for a story. An interface so that the
/// engine can be swapped (this sidecar, Playwright for .NET, a fake in tests)
/// without touching the tools or the runner.
/// </summary>
public interface IBrowser : IAsyncDisposable
{
    Task<string> NavigateAsync(string url);
    Task<string> UrlAsync();
    /// <summary>The page as the model is shown it: visible text plus actionable elements with selectors.</summary>
    Task<string> SnapshotAsync();
    /// <summary>The text of one element, read fresh from the page.</summary>
    Task<string> ElementTextAsync(string selector);
    Task<string> ClickAsync(string selector);
    Task FillAsync(string selector, string value);
    Task ScreenshotAsync(string path);
    /// <summary>Uncaught script errors the page has thrown so far.</summary>
    Task<string> PageErrorsAsync();
}

/// <summary>
/// Drives a real Chromium through the Node sidecar in tools/browser-sidecar
/// (Playwright), one JSON line per command. Needs Node and "npm install" in
/// that folder.
/// </summary>
public sealed class SidecarBrowser : IBrowser
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly Process _process;
    private int _nextId;

    private SidecarBrowser(Process process) => _process = process;

    public static async Task<SidecarBrowser> StartAsync(string sidecarScriptPath)
    {
        if (!File.Exists(sidecarScriptPath)) throw new FileNotFoundException("Browser sidecar script not found: " + sidecarScriptPath);

        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(sidecarScriptPath);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start node.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Story testing needs Node.js on the PATH (it runs the Playwright browser sidecar).");
        }

        var browser = new SidecarBrowser(process);
        try
        {
            await browser.SendAsync("open");
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    public Task<string> NavigateAsync(string url) => SendAsync("navigate", new { url });
    public Task<string> UrlAsync() => SendAsync("url");
    public Task<string> SnapshotAsync() => SendAsync("snapshot");
    public Task<string> ElementTextAsync(string selector) => SendAsync("describe", new { selector });
    public Task<string> ClickAsync(string selector) => SendAsync("click", new { selector });
    public Task FillAsync(string selector, string value) => SendAsync("fill", new { selector, value });
    public Task ScreenshotAsync(string path) => SendAsync("screenshot", new { path });
    public Task<string> PageErrorsAsync() => SendAsync("pageErrors");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await SendAsync("close");
                if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        _process.Dispose();
    }

    private async Task<string> SendAsync(string command, object? arguments = null)
    {
        var id = ++_nextId;
        var request = JsonSerializer.Serialize(new { id, cmd = command, args = arguments ?? new { } });
        await _process.StandardInput.WriteLineAsync(request);
        await _process.StandardInput.FlushAsync();

        var read = _process.StandardOutput.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(CommandTimeout)) != read)
        {
            throw new TimeoutException($"The browser did not answer \"{command}\" within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        var line = await read;
        if (line is null)
        {
            var error = await _process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException("The browser sidecar stopped. " + FirstLines(error));
        }

        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(root.GetProperty("error").GetString());
        }
        return root.GetProperty("result").GetString() ?? "";
    }

    private static string FirstLines(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4)).Trim();
}
