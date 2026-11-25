using System.Collections.Concurrent;

namespace TinyShop.Api;

// TinyShop's backend: the "product code" for the API sample. The harness must never edit it.
// All money is held in pence.

public sealed record Product(string Id, string Name, int PricePence);

public sealed record AddItemRequest(string ProductId);

public sealed record DiscountRequest(string Code);

public sealed record BasketResponse(string Id, string[] Items, int DiscountPercent, int TotalPence);

public sealed record OrderResponse(string OrderId, string Status, int TotalPence);

public sealed record ErrorResponse(string Error);

public sealed class Basket(string id)
{
    public string Id { get; } = id;
    public List<string> Items { get; } = [];
    public int DiscountPercent { get; set; }
    public string? Reference { get; set; }
}

public static class ShopApi
{
    private static readonly Product[] Products =
    [
        new("apple", "Apple", 50),
        new("bread", "Bread", 120),
        new("coffee", "Coffee", 300),
    ];

    /// <summary>Builds the app. Tests call this to host the API in-process on a free port.</summary>
    public static WebApplication Create(string[] args, string? url = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        if (url is not null) app.Urls.Add(url);

        var baskets = new ConcurrentDictionary<string, Basket>();

        app.MapGet("/products", () => Products);

        app.MapPost("/baskets", () =>
        {
            var basket = new Basket(Guid.NewGuid().ToString("N")[..8]);
            baskets[basket.Id] = basket;
            return Results.Created($"/baskets/{basket.Id}", ToResponse(basket));
        });

        app.MapGet("/baskets/{id}", (string id) =>
            baskets.TryGetValue(id, out var basket) ? Results.Ok(ToResponse(basket)) : Results.NotFound(new ErrorResponse("No such basket")));

        app.MapPost("/baskets/{id}/items", (string id, AddItemRequest request) =>
        {
            if (!baskets.TryGetValue(id, out var basket)) return Results.NotFound(new ErrorResponse("No such basket"));
            if (Products.All(p => p.Id != request.ProductId)) return Results.BadRequest(new ErrorResponse("Unknown product"));

            basket.Items.Add(request.ProductId);
            return Results.Ok(ToResponse(basket));
        });

        app.MapPost("/baskets/{id}/discount", (string id, DiscountRequest request) =>
        {
            if (!baskets.TryGetValue(id, out var basket)) return Results.NotFound(new ErrorResponse("No such basket"));
            if (!string.Equals(request.Code, "SAVE10", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErrorResponse("Unknown discount code"));
            }

            basket.DiscountPercent = 10;
            return Results.Ok(ToResponse(basket));
        });

        app.MapPost("/baskets/{id}/checkout", (string id) =>
        {
            if (!baskets.TryGetValue(id, out var basket)) return Results.NotFound(new ErrorResponse("No such basket"));
            if (basket.Items.Count == 0) return Results.Conflict(new ErrorResponse("The basket is empty"));

            var orderId = Guid.NewGuid().ToString("N")[..8];
            return Results.Ok(new OrderResponse(orderId, "placed", TotalPence(basket)));
        });

        return app;
    }

    private static BasketResponse ToResponse(Basket basket) =>
        new(basket.Id, basket.Items.ToArray(), basket.DiscountPercent, TotalPence(basket));

    private static int TotalPence(Basket basket)
    {
        var subtotal = basket.Items.Sum(item => Products.Single(p => p.Id == item).PricePence);
        return (int)Math.Round(subtotal * (100 - basket.DiscountPercent) / 100.0);
    }
}
