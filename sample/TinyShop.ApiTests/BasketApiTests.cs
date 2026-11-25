using System.Net;
using NUnit.Framework;

namespace TinyShop.ApiTests;

[TestFixture]
public sealed class BasketApiTests : ApiTestBase
{
    [Test]
    public async Task AddingItemsAddsUpTheTotal()
    {
        var basket = await Shop.CreateBasketAsync();
        await Shop.AddItemAsync(basket.Id, "apple");
        basket = await Shop.AddItemAsync(basket.Id, "coffee");

        Assert.That(basket.Items, Has.Length.EqualTo(2));
        Assert.That(basket.TotalPence, Is.EqualTo(350));
    }

    [Test]
    public async Task DiscountCodeReducesTheTotal()
    {
        var basket = await Shop.CreateBasketAsync();
        await Shop.AddItemAsync(basket.Id, "coffee");
        basket = await Shop.ApplyDiscountAsync(basket.Id, "SAVE10");

        Assert.That(basket.TotalPence, Is.EqualTo(270));
    }

    [Test]
    public async Task UnknownDiscountCodeIsRejected()
    {
        var basket = await Shop.CreateBasketAsync();
        await Shop.AddItemAsync(basket.Id, "coffee");

        var response = await Shop.PostDiscountAsync(basket.Id, "FREE");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
