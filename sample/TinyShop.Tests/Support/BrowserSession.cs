using Microsoft.Playwright;

namespace TinyShop.Tests.Support;

/// <summary>
/// Holds the Playwright page for one scenario. Reqnroll creates one instance
/// per scenario and injects it into the hooks and step classes.
/// </summary>
public sealed class BrowserSession
{
    private IPage? _page;

    public IPage Page
    {
        get => _page ?? throw new InvalidOperationException("The page has not been opened yet.");
        set => _page = value;
    }

    public bool IsOpen => _page is not null;

    /// <summary>
    /// Uncaught script errors and console errors raised by the page. The hooks
    /// print them when the scenario ends, so they land in the test output where
    /// triage can see them. Without this, a crashed click handler looks exactly
    /// like a stale locator: both end in a timeout.
    /// </summary>
    public List<string> BrowserErrors { get; } = [];
}
