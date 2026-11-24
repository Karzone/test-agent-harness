using Microsoft.Playwright;

namespace TinyShop.Tests.Pages;

/// <summary>Page object for the single TinyShop page.</summary>
public sealed class ShopPage(IPage page)
{
    private ILocator AddButton(string product) =>
        page.Locator($"[data-testid='add-{product.ToLowerInvariant()}']");

    public ILocator BasketCount => page.Locator("[data-testid='basket-count']");
    public ILocator BasketTotal => page.Locator("[data-testid='basket-total']");
    public ILocator DiscountCode => page.Locator("[data-testid='discount-code']");
    public ILocator ApplyDiscountButton => page.Locator("[data-testid='apply-discount']");
    public ILocator CheckoutButton => page.Locator("[data-testid='checkout']");
    public ILocator Confirmation => page.Locator("[data-testid='confirmation']");

    public Task AddToBasketAsync(string product) => AddButton(product).ClickAsync();

    public async Task ApplyDiscountAsync(string code)
    {
        await DiscountCode.FillAsync(code);
        await ApplyDiscountButton.ClickAsync();
    }

    public Task CheckOutAsync() => CheckoutButton.ClickAsync();
}
