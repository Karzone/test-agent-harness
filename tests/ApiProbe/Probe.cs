// A package-free mirror of the NUnit tests in sample/TinyShop.ApiTests: the same
// test client, the same in-process hosting, the same calls and expected values.
// It exists so the harness can be tested end to end on backend failures where
// NuGet (and so NUnit) is not available. Keep it in step with the real tests.
//
// Usage: dotnet run --project tests/ApiProbe [-- <text a test name must contain>]
// Output: one line per test, "PASS|<name>" or "FAIL|<name>|<message>".

using System.Net;
using Microsoft.AspNetCore.Builder;
using TinyShop.Api;
using TinyShop.ApiTests;

const string Prefix = "TinyShop.ApiTests.";
var only = args.FirstOrDefault();

var app = ShopApi.Create([], "http://127.0.0.1:0");
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
var shop = new ShopApiClient(http);

async Task Test(string name, Func<Task> body)
{
    var fullName = Prefix + name;
    if (only is not null && !fullName.Contains(only, StringComparison.Ordinal)) return;

    try
    {
        await body();
        Console.WriteLine($"PASS|{fullName}");
    }
    catch (Exception error)
    {
        var message = error is AssertionException ? error.Message : $"{error.GetType().FullName} : {error.Message}";
        Console.WriteLine($"FAIL|{fullName}|{message.ReplaceLineEndings("\\n")}");
    }
}

// Worded like NUnit's own failure message, so triage sees what it would see for real.
void AreEqual<T>(T expected, T actual)
{
    if (!Equals(expected, actual)) throw new AssertionException($"  Expected: {expected}\n  But was:  {actual}");
}

await Test("CatalogueApiTests.ListsThreeProducts", async () =>
{
    var products = await shop.GetProductsAsync();
    AreEqual("apple,bread,coffee", string.Join(',', products.Select(p => p.Id).Order()));
});

await Test("BasketApiTests.AddingItemsAddsUpTheTotal", async () =>
{
    var basket = await shop.CreateBasketAsync();
    await shop.AddItemAsync(basket.Id, "apple");
    basket = await shop.AddItemAsync(basket.Id, "coffee");
    AreEqual(2, basket.Items.Length);
    AreEqual(350, basket.TotalPence);
});

await Test("BasketApiTests.DiscountCodeReducesTheTotal", async () =>
{
    var basket = await shop.CreateBasketAsync();
    await shop.AddItemAsync(basket.Id, "coffee");
    basket = await shop.ApplyDiscountAsync(basket.Id, "SAVE10");
    AreEqual(270, basket.TotalPence);
});

await Test("BasketApiTests.UnknownDiscountCodeIsRejected", async () =>
{
    var basket = await shop.CreateBasketAsync();
    await shop.AddItemAsync(basket.Id, "coffee");
    var response = await shop.PostDiscountAsync(basket.Id, "FREE");
    AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
});

await Test("CheckoutApiTests.CheckoutPlacesTheOrder", async () =>
{
    var basket = await shop.CreateBasketAsync();
    await shop.AddItemAsync(basket.Id, "bread");
    var order = await shop.CheckOutAsync(basket.Id);
    AreEqual("placed", order.Status);
    AreEqual(120, order.TotalPence);
});

await app.StopAsync();

internal sealed class AssertionException(string message) : Exception(message);
