# test-agent-harness: capability test report

17 of 17 capability tests passed.

Every model in these tests is scripted: a fixed sequence of tool calls, chosen to show how the harness reacts. The UI auto-fix, triage and learning tests use real git worktrees and patches with a simulated test run. The backend tests compile the test client and call the real sample API over HTTP. The story tests drive a real browser.

| # | Area | Capability | Result | Seconds |
|---|---|---|---|---|
| F1 | Auto-fix | Fix a broken test and prove the fix | Pass | 0.4 |
| F2 | Auto-fix | Turn a rejected shortcut into a proper fix | Pass | 0.2 |
| F3 | Auto-fix | Reject a fix that only passes sometimes | Pass | 0.2 |
| T1 | Triage | Report a product defect instead of 'fixing' the test | Pass | 0.2 |
| T2 | Triage | Overrule a model that blames the product for a test issue | Pass | 0.3 |
| L1 | Learning loop | Replay a known fix without calling the model | Pass | 0.3 |
| L2 | Learning loop | Make a person's correction stick | Pass | 0.3 |
| B1 | Backend tests | Fix an API test after a contract change, without touching the expected value | Pass | 14.0 |
| B2 | Backend tests | Recognise a moved endpoint and fix the test client | Pass | 11.7 |
| B3 | Backend tests | Report a wrong calculation in the API as a product defect | Pass | 1.9 |
| B4 | Backend tests | Report a 500 from the API as a product defect | Pass | 1.9 |
| B5 | Backend tests | Leave everything alone when the API cannot be reached | Pass | 0.1 |
| E1 | Eval | Score every planted failure against its known answer | Pass | 26.8 |
| S1 | Story testing | Check a story on a healthy app | Pass | 0.9 |
| S2 | Story testing | Find a product defect while checking a story | Pass | 0.8 |
| S3 | Story testing | Refuse a 'pass' that has no evidence | Pass | 0.9 |
| S4 | Story testing | Keep the tester inside the story's limits | Pass | 0.9 |

## F1. Fix a broken test and prove the fix

- **Test:** Planted failure 01 (a renamed test id). The scripted model first tries to cheat, then makes the real fix.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Fixed on attempt 2 of 2. Attempt 1 was thrown away. The user's checkout was not touched.

Attempt 1, edits the guardrails refused:
- sample/TinyShop.Web/index.html is not test code. Only files under [sample/TinyShop.Tests] may be changed; product code is off limits.
- sample/TinyShop.Tests/Steps/ShopSteps.cs: the change removes an assertion.
- sample/TinyShop.Tests/Steps/ShopSteps.cs: the change adds a fixed wait (sleep or delay); use a retrying assertion instead.

Attempt 1, the model then said: "Fixed. The test passes now."
Verifier: The target test still fails (run 1 of 3).

Attempt 2, verifier: 1 file(s) changed, about 2 line(s); diff obeys the patch policy; target test passed 3 of 3 runs; 4 neighbouring test(s) checked, none newly broken

Verified patch:
```diff
+++ b/sample/TinyShop.Tests/Pages/ShopPage.cs
@@ -9,7 +9,7 @@ public sealed class ShopPage(IPage page)
-    public ILocator BasketTotal => page.Locator("[data-testid='basket-total']");
+    public ILocator BasketTotal => page.Locator("[data-testid='order-total']");
```

## F2. Turn a rejected shortcut into a proper fix

- **Test:** Planted failure 03 (the total now arrives late). The scripted model reaches for a sleep, is refused, and switches to a retrying assertion.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Fixed in one attempt. The sleep was refused inside the loop and the model corrected course.

Refused edit: sample/TinyShop.Tests/Steps/ShopSteps.cs: the change adds a fixed wait (sleep or delay); use a retrying assertion instead.

Verified patch:
```diff
+++ b/sample/TinyShop.Tests/Steps/ShopSteps.cs
@@ -44,10 +44,9 @@ public sealed class ShopSteps(BrowserSession session)
-        // Reads the text once, straight away. Fine while the total updates
-        // synchronously; planted failure 03 shows what happens when it doesn't.
-        var actual = await Shop.BasketTotal.InnerTextAsync();
-        Assert.That(actual, Is.EqualTo(expected));
+        // Retries until the total settles, so it works whether the app updates
+        // the total straight away or a moment later.
+        await Expect(Shop.BasketTotal).ToHaveTextAsync(expected);
```

## F3. Reject a fix that only passes sometimes

- **Test:** A correct-looking fix whose test passes on the first verification run and fails on the second.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Not fixed, and no patch was offered.

Verifier: The target test is flaky: it passed, then failed (run 2 of 3).

## T1. Report a product defect instead of 'fixing' the test

- **Test:** Planted failure 05 (the checkout handler crashes). A model that fails the test if it is ever called.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Product defect, decided by the rule BrowserScriptError with 0 model calls. No patch produced.

Triage reason: The application threw a script error during the test: pageerror: ReferenceError: order is not defined

## T2. Overrule a model that blames the product for a test issue

- **Test:** Planted failure 01. The scripted model first declares a product defect; the rules have evidence it is a stale locator.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Fixed. The model's 'product defect' verdict was overruled by the rule LocatorTimeoutAfterMarkupChange and it was made to try again.

What the harness told the model: You concluded this is a product defect, but the evidence says it is a test issue (LocatorTimeoutAfterMarkupChange): An element was not found and the change touched markup (sample/TinyShop.Web/index.html), which points to a stale locator. Fix the test.

## L1. Replay a known fix without calling the model

- **Test:** Planted failure 01 twice with the same memory file. The second run uses a model that fails the test if called.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Run 1: fixed with 3 model calls. Run 2: fixed with 0 model calls, by replaying the remembered patch and verifying it again.

Run 2 summary: Replayed a remembered fix and verified it again, with no model call: 1 file(s) changed, about 2 line(s); diff obeys the patch policy; target test passed 3 of 3 runs; 4 neighbouring test(s) checked, none newly broken.

## L2. Make a person's correction stick

- **Test:** Planted failure 03 with no matching rule. Run 1: the model wrongly reports a product defect. A person labels it a test issue. Run 2: the model says the same thing again.
- **Ran on:** Real git worktrees and patches. Scripted model. Simulated test run.
- **Result:** Run 1: reported as a product defect (wrong), and nothing was stored from the model's opinion. After the label, run 2: classified by 'human label', the model's repeat verdict was overruled, and the test was fixed.

Run 1 triage source: model
Run 2 triage source: human label
Run 2 attempts: 2 (overruled once, then fixed)

## B1. Fix an API test after a contract change, without touching the expected value

- **Test:** Planted failure 06 (the API renamed a response field). The scripted model first tries to change the expected total, then maps the renamed field.
- **Ran on:** Real API hosted in-process, real HTTP calls, real compile of the test client, real git worktrees. Scripted model. Test methods mirrored in a package-free probe.
- **Result:** Fixed in one attempt. Changing the expected total was refused; the fix went into the test's DTO instead.

Original failure: Expected: 350   But was:  0

Refused edit: sample/TinyShop.ApiTests/BasketApiTests.cs: the change rewrites an expected value in an assertion (350). What the product should do is not the harness's decision; if the expectation really is out of date, a person must change it.

Verifier: 1 file(s) changed, about 6 line(s); diff obeys the patch policy; target test passed 3 of 3 runs; 5 neighbouring test(s) checked, none newly broken

Verified patch:
```diff
+++ b/sample/TinyShop.ApiTests/ShopApiClient.cs
@@ -6,7 +6,11 @@ namespace TinyShop.ApiTests;
-public sealed record BasketDto(string Id, string[] Items, int DiscountPercent, int TotalPence);
+public sealed record BasketDto(
+    string Id,
+    string[] Items,
+    int DiscountPercent,
+    [property: System.Text.Json.Serialization.JsonPropertyName("totalInPence")] int TotalPence);
```

## B2. Recognise a moved endpoint and fix the test client

- **Test:** Planted failure 07 (the discount endpoint moved from /discount to /discounts).
- **Ran on:** Real API hosted in-process, real HTTP calls, real compile of the test client, real git worktrees. Scripted model. Test methods mirrored in a package-free probe.
- **Result:** Fixed. Triaged as a test issue by the rule NotFoundAfterRouteChange before the model was asked.

Original failure: System.Net.Http.HttpRequestException : Response status code does not indicate success: 404 (Not Found).

Triage reason: A request returned 404 and the change altered a route (app.MapPost("/baskets/{id}/discount", (string id, DiscountRequest request) =>), so the test is calling an address that moved.

Verifier: 1 file(s) changed, about 2 line(s); diff obeys the patch policy; target test passed 3 of 3 runs; 5 neighbouring test(s) checked, none newly broken

Verified patch:
```diff
+++ b/sample/TinyShop.ApiTests/ShopApiClient.cs
@@ -35,7 +35,7 @@ public sealed class ShopApiClient(HttpClient http)
-        http.PostAsJsonAsync($"/baskets/{basketId}/discount", new { code });
+        http.PostAsJsonAsync($"/baskets/{basketId}/discounts", new { code });
```

## B3. Report a wrong calculation in the API as a product defect

- **Test:** Planted failure 08 (the API applies SAVE10 as 10p off instead of 10%). A model that fails the test if it is ever called.
- **Ran on:** Real API hosted in-process, real HTTP calls, real compile of the test client, real git worktrees. Scripted model. Test methods mirrored in a package-free probe.
- **Result:** Product defect, decided by the rule BehaviourChangedByRefactor with 0 model calls. No patch produced.

Original failure: Expected: 270   But was:  290

Triage reason: The change was described as having no behaviour change, but an assertion now sees a different value after product code changed: sample/TinyShop.Api/ShopApi.cs

## B4. Report a 500 from the API as a product defect

- **Test:** Planted failure 09 (the checkout endpoint throws). A model that fails the test if it is ever called.
- **Ran on:** Real API hosted in-process, real HTTP calls, real compile of the test client, real git worktrees. Scripted model. Test methods mirrored in a package-free probe.
- **Result:** Product defect, decided by the rule ServerError with 0 model calls. No patch produced.

Original failure: System.Net.Http.HttpRequestException : Response status code does not indicate success: 500 (Internal Server Error).

Triage reason: The application returned a server error: 500 (Internal Server Error).

## B5. Leave everything alone when the API cannot be reached

- **Test:** A test run that fails with 'Connection refused'. A model that fails the test if it is ever called.
- **Ran on:** Real git worktree. Canned test result (the sample API is hosted in-process, so it cannot be down).
- **Result:** Environment problem, decided by the rule EnvironmentUnavailable with 0 model calls. Nothing was changed.

Triage reason: The system under test could not be reached: Connection refused (localhost:5001)

## E1. Score every planted failure against its known answer

- **Test:** All nine planted failures (five UI, four API), each with its scripted model.
- **Ran on:** UI failures: simulated test run. API failures: real API and real HTTP calls. Scripted models.
- **Result:** 9 of 9 correct. False fixes: 0. Wrongly blocked: 0. Decided without the model: 4.

| Failure | Expected | Outcome | Verdict | Decided by | Model calls |
|---|---|---|---|---|---|
| 01-renamed-locator | TestIssue | Fixed | CorrectFix | LocatorTimeoutAfterMarkupChange | 10 |
| 02-reworded-step | TestIssue | Fixed | CorrectFix | MissingStepBinding | 4 |
| 03-async-total | TestIssue | Fixed | CorrectFix | none | 5 |
| 04-wrong-discount | ProductDefect | ProductDefect | CorrectDefect | BehaviourChangedByRefactor | 0 |
| 05-checkout-crash | ProductDefect | ProductDefect | CorrectDefect | BrowserScriptError | 0 |
| 06-renamed-total-field | TestIssue | Fixed | CorrectFix | none | 6 |
| 07-moved-discount-route | TestIssue | Fixed | CorrectFix | NotFoundAfterRouteChange | 4 |
| 08-wrong-discount-api | ProductDefect | ProductDefect | CorrectDefect | BehaviourChangedByRefactor | 0 |
| 09-checkout-crash-api | ProductDefect | ProductDefect | CorrectDefect | ServerError | 0 |

## S1. Check a story on a healthy app

- **Test:** The discount-code story (3 acceptance criteria) against the unmodified sample shop.
- **Ran on:** Real Chromium via Playwright. Scripted model.
- **Result:** Passed: 3 of 3 criteria, each with evidence the harness read from the page and a screenshot.

| Criterion | Result | Evidence read from the page |
|---|---|---|
| AC1: Adding Coffee to the basket shows a basket total of £3.00 | Passed | £3.00 |
| AC2: Applying the discount code SAVE10 reduces the total by 10%, to £2.70 | Passed | £2.70 |
| AC3: The basket shows how many items it holds | Passed (review) | 1 |

## S2. Find a product defect while checking a story

- **Test:** The same story against the shop with planted defect 04 (SAVE10 takes 10p off instead of 10%).
- **Ran on:** Real Chromium via Playwright. Scripted model.
- **Result:** Failed: AC2 shows £2.90 where the story expects £2.70. AC1 and AC3 passed.

The tester first tried to record AC2 as a pass. The harness answered: Not recorded: you said pass, but [data-testid='basket-total'] shows "£2.90", not the expected "£2.70". Record what the page really shows.

| Criterion | Result | Evidence read from the page |
|---|---|---|
| AC1: Adding Coffee to the basket shows a basket total of £3.00 | Passed | £3.00 |
| AC2: Applying the discount code SAVE10 reduces the total by 10%, to £2.70 | Failed | £2.90 |
| AC3: The basket shows how many items it holds | Passed (review) | 1 |

## S3. Refuse a 'pass' that has no evidence

- **Test:** The defective shop again, with a scripted tester that announces success without recording AC2.
- **Ran on:** Real Chromium via Playwright. Scripted model.
- **Result:** Incomplete, not passed: AC2 is reported as unverified.

The tester's final answer: "All three acceptance criteria pass. The story is done."

| Criterion | Result | Evidence read from the page |
|---|---|---|
| AC1: Adding Coffee to the basket shows a basket total of £3.00 | Passed | £3.00 |
| AC2: Applying the discount code SAVE10 reduces the total by 10%, to £2.70 | Unverified | No result backed by evidence from the page was recorded for this criterion. |
| AC3: The basket shows how many items it holds | Passed (review) | 1 |

## S4. Keep the tester inside the story's limits

- **Test:** A scripted tester that tries to open another website and to click the forbidden Checkout button before doing the job.
- **Ran on:** Real Chromium via Playwright. Scripted model.
- **Result:** Both off-limits actions were blocked before reaching the browser, and the story still passed 3 of 3.

- Blocked by guardrail: https://example.com/ is outside the area this story may visit (file:///<repo>/sample/TinyShop.Web/).
- Blocked by guardrail: "Checkout" is a forbidden action for this story ("checkout"). Nothing was clicked.
