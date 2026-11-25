using NUnit.Framework;

namespace TinyShop.ApiTests;

[TestFixture]
public sealed class CatalogueApiTests : ApiTestBase
{
    [Test]
    public async Task ListsThreeProducts()
    {
        var products = await Shop.GetProductsAsync();

        Assert.That(products.Select(p => p.Id), Is.EquivalentTo(new[] { "apple", "bread", "coffee" }));
    }
}

[TestFixture]
public sealed class CheckoutApiTests : ApiTestBase
{
    [Test]
    public async Task CheckoutPlacesTheOrder()
    {
        var basket = await Shop.CreateBasketAsync();
        await Shop.AddItemAsync(basket.Id, "bread");

        var order = await Shop.CheckOutAsync(basket.Id);

        Assert.That(order.Status, Is.EqualTo("placed"));
        Assert.That(order.TotalPence, Is.EqualTo(120));
    }
}
