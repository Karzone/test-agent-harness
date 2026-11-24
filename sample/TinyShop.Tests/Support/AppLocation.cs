namespace TinyShop.Tests.Support;

/// <summary>
/// Finds the sample app on disk. TinyShop is static HTML, so the tests open it
/// with a file:// URL and no web server is needed.
/// </summary>
public static class AppLocation
{
    public static string IndexUrl { get; } = new Uri(FindIndexHtml()).AbsoluteUri;

    private static string FindIndexHtml()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "TinyShop.Web", "index.html");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find TinyShop.Web/index.html above " + AppContext.BaseDirectory);
    }
}
