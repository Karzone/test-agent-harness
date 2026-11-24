using Microsoft.Playwright;
using Reqnroll;

namespace TinyShop.Tests.Support;

[Binding]
public sealed class Hooks(BrowserSession session)
{
    private const float TimeoutMs = 5000;

    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    [BeforeTestRun]
    public static async Task StartBrowser()
    {
        Assertions.SetDefaultExpectTimeout(TimeoutMs);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    [AfterTestRun]
    public static async Task StopBrowser()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [BeforeScenario]
    public async Task OpenShop()
    {
        var page = await _browser!.NewPageAsync();
        page.SetDefaultTimeout(TimeoutMs);

        var errors = session.BrowserErrors;
        page.PageError += (_, error) => { lock (errors) errors.Add($"[browser] pageerror: {error}"); };
        page.Console += (_, message) =>
        {
            if (message.Type == "error") lock (errors) errors.Add($"[browser] console.error: {message.Text}");
        };

        await page.GotoAsync(AppLocation.IndexUrl);
        session.Page = page;
    }

    [AfterScenario]
    public async Task ClosePage()
    {
        // Printed here, on the test's own thread, so the test framework records it.
        lock (session.BrowserErrors)
        {
            foreach (var error in session.BrowserErrors) Console.WriteLine(error);
        }

        if (session.IsOpen) await session.Page.CloseAsync();
    }
}
