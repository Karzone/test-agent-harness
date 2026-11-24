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
}
