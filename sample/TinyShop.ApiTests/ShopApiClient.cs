using System.Net.Http.Json;

namespace TinyShop.ApiTests;

// What the tests expect the API to send back. These mirror the API's contract;
// when the contract changes on purpose, this is the file that goes stale.
public sealed record ProductDto(string Id, string Name, int PricePence);

public sealed record BasketDto(string Id, string[] Items, int DiscountPercent, int TotalPence);

public sealed record OrderDto(string OrderId, string Status, int TotalPence);

/// <summary>
/// The tests' view of the API: one method per endpoint. The API equivalent of
/// a page object, so routes and payload shapes live in one place.
/// </summary>
public sealed class ShopApiClient(HttpClient http)
{
    public async Task<ProductDto[]> GetProductsAsync() =>
        (await http.GetFromJsonAsync<ProductDto[]>("/products"))!;

    public async Task<BasketDto> CreateBasketAsync()
    {
        var response = await http.PostAsync("/baskets", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BasketDto>())!;
    }

    public async Task<BasketDto> AddItemAsync(string basketId, string productId)
    {
        var response = await http.PostAsJsonAsync($"/baskets/{basketId}/items", new { productId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BasketDto>())!;
    }

    /// <summary>Returns the raw response so tests can assert on rejected codes as well as accepted ones.</summary>
    public Task<HttpResponseMessage> PostDiscountAsync(string basketId, string code) =>
        http.PostAsJsonAsync($"/baskets/{basketId}/discount", new { code });

    public async Task<BasketDto> ApplyDiscountAsync(string basketId, string code)
    {
        var response = await PostDiscountAsync(basketId, code);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BasketDto>())!;
    }

    public async Task<OrderDto> CheckOutAsync(string basketId)
    {
        var response = await http.PostAsync($"/baskets/{basketId}/checkout", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }
}
