using Microsoft.AspNetCore.Builder;
using NUnit.Framework;
using TinyShop.Api;

namespace TinyShop.ApiTests;

/// <summary>
/// Hosts the API inside the test process on a free port, once per test class.
/// Nothing has to be deployed or started beforehand, and a failed request is
/// the API's doing, not the network's.
/// </summary>
public abstract class ApiTestBase
{
    private WebApplication? _app;
    private HttpClient? _http;

    protected ShopApiClient Shop { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartApi()
    {
        _app = ShopApi.Create([], "http://127.0.0.1:0");
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
        Shop = new ShopApiClient(_http);
    }

    [OneTimeTearDown]
    public async Task StopApi()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
