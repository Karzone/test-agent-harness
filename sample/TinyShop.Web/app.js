// TinyShop: the "product code" in this sample. The harness must never edit it.
// All money is held in pence to avoid floating-point surprises.

const PRICES = { apple: 50, bread: 120, coffee: 300 };

const basket = [];
let discountPercent = 0;

const el = (testId) => document.querySelector(`[data-testid="${testId}"]`);
const money = (pence) => `£${(pence / 100).toFixed(2)}`;

function totalPence() {
  const subtotal = basket.reduce((sum, product) => sum + PRICES[product], 0);
  return Math.round(subtotal * (100 - discountPercent) / 100);
}

function render() {
  el("basket-count").textContent = String(basket.length);
  el("basket-total").textContent = money(totalPence());
}

for (const product of Object.keys(PRICES)) {
  el(`add-${product}`).addEventListener("click", () => {
    basket.push(product);
    render();
  });
}

el("apply-discount").addEventListener("click", () => {
  const code = el("discount-code").value.trim().toUpperCase();
  discountPercent = code === "SAVE10" ? 10 : 0;
  render();
});

el("checkout").addEventListener("click", () => {
  const confirmation = el("confirmation");
  const noun = basket.length === 1 ? "item" : "items";
  confirmation.textContent = basket.length === 0
    ? "Your basket is empty"
    : `Order placed: ${basket.length} ${noun}, total ${money(totalPence())}`;
  confirmation.hidden = false;
});

render();
