// Browser sidecar: drives one Chromium page with Playwright and takes commands
// as JSON lines on stdin ({ id, cmd, args }), answering on stdout
// ({ id, ok, result } or { id, ok: false, error }).
//
// The harness owns this process: it starts it, sends every command, and kills
// it when the story run ends. The model never talks to it directly.

import { createRequire } from "node:module";
import { createInterface } from "node:readline";

// TAH_PLAYWRIGHT_DIR lets the harness point at a Playwright installed somewhere
// else (for example a global one); by default it is resolved from this folder.
const base = process.env.TAH_PLAYWRIGHT_DIR ? process.env.TAH_PLAYWRIGHT_DIR.replace(/\/?$/, "/") : import.meta.url;
const { chromium } = createRequire(base)("playwright");

const TIMEOUT_MS = 5000;
let browser;
let page;
const pageErrors = [];

const commands = {
  async open() {
    browser = await chromium.launch({ headless: true });
    page = await browser.newPage({ viewport: { width: 900, height: 700 } });
    page.setDefaultTimeout(TIMEOUT_MS);
    page.on("pageerror", (error) => pageErrors.push(String(error)));
    return "opened";
  },

  async navigate({ url }) {
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 15000 });
    return page.url();
  },

  url: async () => page.url(),

  // The visible text of the page. The harness uses this, not the model's word,
  // to check that quoted evidence is really on screen.
  async text() {
    return (await page.innerText("body")).replace(/\n{2,}/g, "\n").trim();
  },

  // What the model is shown: the visible text and the things it can act on,
  // each with a selector it can pass straight back to click or fill.
  async snapshot() {
    return page.evaluate(() => {
      const visible = (el) => {
        const rect = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
      };
      const quote = (value) => value.replace(/'/g, "\\'");
      const selectorFor = (el) => {
        const testId = el.getAttribute("data-testid");
        if (testId) return `[data-testid='${quote(testId)}']`;
        if (el.id) return `#${CSS.escape(el.id)}`;
        const name = el.getAttribute("name");
        if (name) return `${el.tagName.toLowerCase()}[name='${quote(name)}']`;
        const text = (el.innerText || "").trim().slice(0, 40);
        return text ? `text=${text}` : el.tagName.toLowerCase();
      };
      const elements = [...document.querySelectorAll("a, button, input, select, textarea, [role='button']")]
        .filter(visible)
        .slice(0, 60)
        .map((el) => {
          const label = (el.innerText || el.getAttribute("aria-label") || el.getAttribute("placeholder") || el.closest("label")?.innerText || "").trim().slice(0, 60);
          const value = "value" in el && el.value ? ` value="${String(el.value).slice(0, 40)}"` : "";
          return `${el.tagName.toLowerCase()} ${selectorFor(el)} "${label}"${value}`;
        });
      return [
        `Title: ${document.title}`,
        `URL: ${location.href}`,
        "",
        "Visible text:",
        document.body.innerText.replace(/\n{2,}/g, "\n").trim().slice(0, 3000),
        "",
        "Elements you can act on:",
        ...elements,
      ].join("\n");
    });
  },

  // The text of an element, so the harness can refuse a forbidden click before it happens.
  async describe({ selector }) {
    const element = page.locator(selector).first();
    const text = (await element.innerText({ timeout: TIMEOUT_MS }).catch(() => "")) || "";
    const label = (await element.getAttribute("aria-label", { timeout: TIMEOUT_MS }).catch(() => "")) || "";
    return `${text} ${label}`.trim();
  },

  async click({ selector }) {
    await page.locator(selector).first().click();
    await page.waitForLoadState("domcontentloaded");
    return page.url();
  },

  async fill({ selector, value }) {
    await page.locator(selector).first().fill(value);
    return "filled";
  },

  async screenshot({ path }) {
    await page.screenshot({ path, fullPage: true });
    return path;
  },

  pageErrors: async () => pageErrors.join("\n"),

  async close() {
    await browser?.close();
    return "closed";
  },
};

const lines = createInterface({ input: process.stdin });
for await (const line of lines) {
  if (!line.trim()) continue;
  let request;
  try {
    request = JSON.parse(line);
    const handler = commands[request.cmd];
    if (!handler) throw new Error(`Unknown command: ${request.cmd}`);
    const result = await handler(request.args ?? {});
    process.stdout.write(JSON.stringify({ id: request.id, ok: true, result }) + "\n");
  } catch (error) {
    const message = String(error?.message ?? error).split("\n")[0];
    process.stdout.write(JSON.stringify({ id: request?.id ?? null, ok: false, error: message }) + "\n");
  }
  if (request?.cmd === "close") break;
}
process.exit(0);
