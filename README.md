# test-agent-harness

An agent harness for testing work, built one step at a time in C#. It has two modes:

- `tah fix` repairs a failing automation test (UI or API), or reports a product defect or an environment problem.
- `tah story` checks a user story in a real browser, the way a tester would by hand.

Give `tah fix` a failing test. It answers with one of three things:

- a **verified patch** to the test code,
- a **defect report** saying the test is right and the product is wrong, or
- an **environment report** saying the system under test could not be reached, with nothing changed.

It is the code *around* a model, not a model: tools, guardrails, an isolated workspace, verification, triage rules, a memory and an eval. The model is one interface with one method, and any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, OpenRouter) can sit behind it.

The idea and the step-by-step format come from Tejas Kumar's [basically-ai-harness](https://github.com/TejasQ/basically-ai-harness) and his talk "Harnesses in AI: A Deep Dive". Steps 1 to 5 follow that repository's shape, applied to test repair. Steps 6 to 8 go further.

## Status: what has and has not been run

| Part | State |
|---|---|
| Harness core, CLI, eval | Compiled and covered by 56 self-tests, including end-to-end runs against real git worktrees. |
| Story testing (`tah story`) | Run end to end in a real Chromium against the sample page, with scripted testers. |
| Backend (API) support | The sample API is real and runs. Fixes were verified by compiling the test client and calling the API over HTTP, through a package-free mirror of the API tests (`tests/ApiProbe`). |
| Sample API test suite (`sample/TinyShop.ApiTests`) | **Written but never compiled**, for the same reason as the UI suite: it needs NUnit from NuGet. Its logic is the same as the probe's, which does run. |
| Sample web page and its five planted failures | Checked in a real browser: the app is green, and each planted failure breaks exactly the scenarios it should. |
| Sample API and its four planted failures | Checked over real HTTP: green when healthy, and each planted failure breaks exactly the tests it should. |
| Sample Reqnroll + Playwright suite (`sample/TinyShop.Tests`) | **Written but never compiled.** The environment it was built in could not reach NuGet. Run it once before relying on it; the package versions and the test names in `failures/manifest.json` are the likeliest things to need a tweak. |
| `dotnet test` runner and TRX parsing | The runner was exercised only as far as a failed restore. The parser is tested against a hand-written TRX file, not one produced by a real run. |
| Live model client | Request and response formats are unit-tested. It has not been called against a live endpoint. |

In the self-tests a simulator stands in for `dotnet test`, and scripted "models" stand in for a real one. `docs/capability-report.md` shows one end-to-end test per capability with the evidence each produced.

## Quick start

```sh
# 1. The harness's own tests. No packages, no browser, no API key.
dotnet run --project tests/TestAgentHarness.SelfTests

# 2. The sample suite. Needs NuGet and a browser.
dotnet build sample/TinyShop.Tests
pwsh sample/TinyShop.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
dotnet test sample/TinyShop.Tests          # expect 4 passing scenarios
dotnet test sample/TinyShop.ApiTests       # expect 5 passing tests (no browser or server needed)

# 3. The whole harness against real "dotnet test", still with no API key:
#    scripted models replay known tool calls for each planted failure.
dotnet run --project src/TestAgentHarness.Cli -- eval --model scripted:failures/scripts

# 4. One failure, watching a badly behaved "model" get blocked and corrected.
dotnet run --project src/TestAgentHarness.Cli -- fix --failure 01-renamed-locator --model scripted:failures/scripts --no-memory

# 5. Story testing in a real browser (needs Node.js).
(cd tools/browser-sidecar && npm install && npx playwright install chromium)
dotnet run --project src/TestAgentHarness.Cli -- story --story sample/stories/discount-code.json --model scripted:sample/stories/scripts/discount-code.json

# 6. A real model.
export TAH_API_KEY=...
export TAH_MODEL=...                 # e.g. an OpenRouter model id or an Azure deployment name
export TAH_BASE_URL=...              # optional; defaults to https://openrouter.ai/api/v1
dotnet run --project src/TestAgentHarness.Cli -- fix --failure 03-async-total
dotnet run --project src/TestAgentHarness.Cli -- eval
```

If step 3 reports `NoTestsMatched`, the test names differ from the filters in `failures/manifest.json`. `dotnet test sample/TinyShop.Tests --list-tests` shows the real names.

## One branch per step

Each step is a branch, so `git diff step-2 step-3` shows exactly what a layer adds. The step branches predate the rename, so on those branches the projects and the command are still called FixHarness and `fixharness run`.

| Branch | Adds | What it fixes about the step before |
|---|---|---|
| `step-1` | Sample app, Reqnroll + Playwright suite, five planted UI failures | A problem with known answers to work on. |
| `step-2` | Tools and the bare agent loop | Nothing yet. The loop can run forever, edits your checkout, and can "fix" a test by deleting its assertion. |
| `step-3` | Guardrails and context trimming | The loop always stops, and edits that cheat are rejected with the reason given back to the model. |
| `step-4` | The harness owns the lifecycle | Work happens in a throwaway git worktree. Your checkout is never touched. |
| `step-5` | Verify and retry | The harness stops believing "fixed". It re-runs the test itself, several times, and checks the neighbours. |
| `step-6` | Triage before fixing | Plain rules decide test issue or product defect first. A defect gets a report, not a patch, and no model call. |
| `step-7` | Learning loop | Repeat failures replay the known fix. Similar failures get hints. A person's correction sticks. |
| `step-8` | Eval | The planted failures become a scoreboard for any change to a prompt, rule, guardrail or model. |

## How a run works

```
open a throwaway workspace
 -> run the test: does it really fail?                 no: stop
 -> triage: a person's label, then rules               product defect or environment problem: report and stop (no model)
 -> replay a remembered fix for this exact failure     verified: done (no model)
 -> agent loop: tools + guardrails + lessons from similar failures
 -> verify: diff obeys the policy, test passes 3 of 3, neighbours unbroken
      rejected: reset the workspace, tell the model why, try again
 -> remember what was learned; remove the workspace
```

The model is the last resort, not the first step.

Added after the step branches, on `main`: story testing, backend (API) tests with their own planted failures and rules, and the environment verdict.

## The parts

### Tools (`Tools/`)

`run_test`, `list_files`, `read_file`, `search`, `replace_in_file`. They are bound to one workspace and one test, both chosen by the harness. Edits replace one exact, unique piece of text, which is easier for a model to get right than a unified diff.

### Guardrails (`Guardrails/`)

Loop guardrails stop a run that is going nowhere: an iteration limit, a time budget, and a check for the same call returning the same result three times in a row.

The patch policy is applied to every edit as it is made, and again to the final diff:

| A fix may not | Why |
|---|---|
| Change anything outside the test roots | Product code is not the harness's to edit. |
| Remove or comment out an assertion, a step or a scenario | The quickest way to green. |
| Skip or ignore a test | The second quickest. |
| Add `Thread.Sleep`, `Task.Delay` or `WaitForTimeoutAsync` | A fixed wait hides a race instead of fixing it. |
| Add an empty `catch` | Swallows the failure. |
| Rewrite what a `Then` step expects, or an expected value on a C# assertion line | That is how a product defect gets "fixed". A person decides what the product should do. Switch off with `AllowExpectationChanges`. |
| Touch more than 3 files or about 40 lines | A test repair is small. A big diff is something else. |

### Verifier (`Harness/Verifier.cs`)

Never reads the model's answer. It checks that something changed, that the diff obeys the policy, that the test passes three times running, and that no neighbouring test that was passing now fails. Zero tests run and skipped tests are never a pass.

### Triage (`Triage/`)

Rules run in order and the first verdict wins.

| Rule | Evidence | Verdict |
|---|---|---|
| `MissingStepBinding` | "No matching step definition" | Test issue |
| `EnvironmentUnavailable` | Connection refused, DNS failure, client timeout, 502/503/504 | Environment |
| `BrowserScriptError` | The page threw an uncaught script error | Product defect |
| `ServerError` | A 500 response | Product defect |
| `OnlyTestCodeChanged` | The preceding change touched only test code | Test issue |
| `NotFoundAfterRouteChange` | A 404, and the change altered a route in product code | Test issue |
| `BehaviourChangedByRefactor` | The change says "no behaviour change", product code changed, an assertion sees a new value | Product defect |
| `LocatorTimeoutAfterMarkupChange` | An element was not found and the change touched markup | Test issue |

A rule is one function; add your own through `FixRunner.TriageRules`.

An **environment** verdict means the system under test could not be reached. Nothing is changed and the model is not called, because neither the test nor the product is at fault.

When no rule matches, the model investigates and must end with `VERDICT: TEST_ISSUE`, `VERDICT: PRODUCT_DEFECT` or `VERDICT: ENVIRONMENT`. Its verdict is handled according to how much evidence there is:

- **Rules or a person say test issue, model says product defect or environment:** the model is overruled, told why, and made to try again.
- **No rule matched, model says product defect or environment:** accepted, but the report says it is a judgement for a person to confirm.

`BrowserScriptError` only works because the sample's hooks print browser errors into the test output (`sample/TinyShop.Tests/Support/Hooks.cs`). Without that, a crashed click handler and a stale locator both look like a timeout. It is worth copying into a real suite.

### Backend (API) tests

Nothing in the fixer is specific to UI tests: it runs whatever `dotnet test` runs. The repository has a second sample to show it, with four planted backend failures.

| Planted failure | What happens | Right answer |
|---|---|---|
| `06-renamed-total-field` | The API renamed a response field, so the test reads 0 | Fix the test's DTO, not the expected total |
| `07-moved-discount-route` | An endpoint moved, so the test gets a 404 | Fix the route in the test client |
| `08-wrong-discount-api` | The API calculates the discount wrongly | Product defect |
| `09-checkout-crash-api` | The checkout endpoint throws and returns 500 | Product defect |

`sample/TinyShop.ApiTests` hosts the API inside the test process, so there is no server to start. For tests that call a deployed environment, the `EnvironmentUnavailable` rule keeps an outage from being treated as something to fix.

A repository with more than one test project tells the harness which one to use with `--project`, `--test-roots` and `--neighbours`; planted failures carry the same settings in `failures/manifest.json`.

### Learning loop (`Memory/`)

What is learned lives in `.testagentharness/memory.jsonl`: plain JSON lines you can read, review in a pull request and edit.

- **Replay.** A failure with the same fingerprint as one fixed before gets that patch applied and fully re-verified. If it passes, the run ends with no model call.
- **Lessons.** A similar failure (same shape, different test or selector) gets the earlier fix, or an earlier rejected attempt, as a hint in the model's context.
- **Labels.** `tah feedback --label test-issue` (or `product-defect`, or `environment`) after a wrong classification. That exact failure is then classified your way every time, above the rules and the model.

Three rules keep it honest:

1. It learns only from evidence: fixes the verifier accepted, attempts the verifier rejected, labels a person gave. A model's opinion is never stored.
2. A remembered fix is never trusted on sight. Replay means the full verifier again.
3. It corrects itself. A remembered fix that fails verification twice stops being replayed.

Fingerprints are plain text processing (timings, line numbers, ids and paths stripped), so the same failure always gets the same one. There are no embeddings and no vector store.

### Story testing (`Story/`)

`tah story --story <file>` gives the model browser tools (snapshot, navigate, click, fill) and a story file:

```json
{
  "title": "Discount code reduces the basket total",
  "startUrl": "sample/TinyShop.Web/index.html",
  "forbiddenActions": ["checkout", "delete", "pay"],
  "criteria": [
    { "id": "AC2", "text": "Applying SAVE10 reduces the total by 10%, to £2.70", "expect": "£2.70" }
  ]
}
```

The same harness ideas apply, with a browser as the environment instead of a git worktree:

- **Evidence, not claims.** The model cannot declare a criterion passed. It calls `record_result` with a selector; the harness reads that element itself, checks it against `expect`, and takes a screenshot. A "pass" the page does not support is refused.
- **Unverified is not passed.** A criterion with no recorded evidence is reported as unverified whatever the model's final answer says, and the story's outcome is `Incomplete`.
- **Guardrails.** Only URLs under the story's allowed area can be opened, and elements whose text matches a forbidden action are never clicked.
- **Review flag.** A criterion with no `expect` cannot be checked by the harness, so its evidence is flagged for a person.

The browser is a small Node script (`tools/browser-sidecar`) driving Playwright, behind the `IBrowser` interface. A Playwright for .NET implementation could replace it without touching the tools or the runner.

### Eval (`Eval/`)

`tah eval` runs every planted failure and scores it against the known answer.

| Verdict | Meaning |
|---|---|
| `CorrectFix`, `CorrectDefect` | Right. |
| `MissedFix` | A test issue was not fixed. Costs time, harms nothing. |
| `DefectNotRecognised` | A defect produced no patch but was not reported either. Safe, not helpful. |
| `WronglyBlocked` | A test issue was reported as a product defect. Auto-fix stops and a false defect is raised. |
| `FalseFix` | A product defect was "fixed" in the test. A real bug is hidden. |

The command exits non-zero on `WronglyBlocked` or `FalseFix`, so it can gate a pipeline. Memory is off during an eval unless you pass `--with-memory`, so runs are comparable.

The nine planted failures (five UI, four API) are a start, not a benchmark. The eval gets useful when you add failures from your own history.

## Using it on your own repository

1. Add `testagentharness.json` at the repository root:

   ```json
   {
     "testProject": "tests/MyApp.UiTests",
     "testRoots": ["tests/MyApp.UiTests"],
     "neighbourFilter": "FullyQualifiedName~MyApp.UiTests.Basket"
   }
   ```

   `testRoots` is the only place a fix may touch. `neighbourFilter` is optional and should be small enough to run after every fix.

2. Make sure generated files are git-ignored, in particular Reqnroll's `*.feature.cs`. The harness treats every new file that is not ignored as part of the fix.

3. Run it on a failing test:

   ```sh
   tah fix --filter "FullyQualifiedName~Basket.TotalAddsUp" --base origin/main --note "PR description here"
   ```

   `--base` gives the harness the diff that preceded the failure. It is the best evidence triage has and the most useful thing the model is shown.

Outputs go to `tah-out/`: `report.md` (ready for a pull-request comment or a defect ticket), `trace.json` (every tool call) and, only for a verified fix, `fix.patch`.

The harness works from the commit at `HEAD`. Uncommitted changes in your checkout are not seen.

## Limits

- A determined model can still write a wrong fix that passes: for example, hard-coding the expected value inside a step definition. The policy closes the common routes, not all of them. The output is a patch for review, never a merge.
- The triage rules are few and written for this sample. They will need extending for a real product, and `BehaviourChangedByRefactor` depends on what people write in pull-request descriptions.
- Only `dotnet test` is supported as a runner. `ITestRunner` is the seam for others.
- The harness runs tests; it does not start the system under test. The sample API tests host the API in-process. Tests against a deployed environment rely on that environment being up.
- `tah story` is browser-only. There is no API equivalent yet.
- One test at a time, one workspace at a time. No parallel runs.
- In `tah fix` the model sees source files and test output only. It has no DOM snapshot or Playwright trace tool yet, which matters for locator fixes in apps whose markup is not in the repository.
- `tah story` checks what the story file makes checkable. A criterion without an `expect` value relies on a person reviewing the evidence, and a story run does not yet produce an automated test from what it did.

## Layout

```
src/TestAgentHarness.Core/      the harness (no package references)
  Model/                  IModelClient, OpenAI-compatible client, scripted client
  Tools/                  the five tools
  Context/                what the model is shown; trimming
  Loop/                   the agent loop
  Guardrails/             loop guardrails, patch policy
  Harness/                workspace, runner, verifier, reports
  Triage/                 rules and verdicts
  Memory/                 fingerprints and the learning store
  Eval/                   the eval runner
  Story/                  story checking: browser interface, tools, runner
  Testing/                dotnet test runner, TRX parser
src/TestAgentHarness.Cli/       the tah command
tests/TestAgentHarness.SelfTests/   tests for the harness (plain console app)
tests/ApiProbe/           package-free mirror of the API tests, used to test the harness
sample/TinyShop.Web/      the "product": one static page
sample/TinyShop.Tests/    Reqnroll + Playwright suite
sample/TinyShop.Api/      the backend "product": a small HTTP API
sample/TinyShop.ApiTests/ NUnit tests for the API
sample/stories/           a sample story and scripted testers
tools/browser-sidecar/    the Playwright browser used by story checking
docs/                     capability test report
failures/                 planted failures, their answers, scripted models
```
