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
        await page.GotoAsync(AppLocation.IndexUrl);
        session.Page = page;
    }

    [AfterScenario]
    public async Task ClosePage()
    {
        if (session.IsOpen) await session.Page.CloseAsync();
    }
}
