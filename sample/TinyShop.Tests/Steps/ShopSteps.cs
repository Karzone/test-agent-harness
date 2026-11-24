using NUnit.Framework;
using Reqnroll;
using TinyShop.Tests.Pages;
using TinyShop.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace TinyShop.Tests.Steps;

[Binding]
public sealed class ShopSteps(BrowserSession session)
{
    private ShopPage Shop => new(session.Page);

    [Given("the shop is open")]
    public async Task GivenTheShopIsOpen()
    {
        await Expect(session.Page).ToHaveTitleAsync("TinyShop");
    }

    [When("I add {string} to the basket")]
    public async Task WhenIAddToTheBasket(string product)
    {
        await Shop.AddToBasketAsync(product);
    }

    [When("I apply the discount code {string}")]
    public async Task WhenIApplyTheDiscountCode(string code)
    {
        await Shop.ApplyDiscountAsync(code);
    }

    [When("I check out")]
    public async Task WhenICheckOut()
    {
        await Shop.CheckOutAsync();
    }

    [Then("the basket count is {int}")]
    public async Task ThenTheBasketCountIs(int expected)
    {
        await Expect(Shop.BasketCount).ToHaveTextAsync(expected.ToString());
    }

    [Then("the basket total is {string}")]
    public async Task ThenTheBasketTotalIs(string expected)
    {
        // Reads the text once, straight away. Fine while the total updates
        // synchronously; planted failure 03 shows what happens when it doesn't.
        var actual = await Shop.BasketTotal.InnerTextAsync();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Then("I see the confirmation {string}")]
    public async Task ThenISeeTheConfirmation(string expected)
    {
        await Expect(Shop.Confirmation).ToHaveTextAsync(expected);
    }
}
