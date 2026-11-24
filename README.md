# FixHarness

An agent harness for failing automation tests, built one step at a time in C#.

Give it a failing test. It answers with one of two things:

- a **verified patch** to the test code, or
- a **defect report** saying the test is right and the product is wrong.

It is the code *around* a model, not a model: tools, guardrails, an isolated workspace, verification, triage rules, a memory and an eval. The model is one interface with one method, and any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, OpenRouter) can sit behind it.

The idea and the step-by-step format come from Tejas Kumar's [basically-ai-harness](https://github.com/TejasQ/basically-ai-harness) and his talk "Harnesses in AI: A Deep Dive". Steps 1 to 5 follow that repository's shape, applied to test repair. Steps 6 to 8 go further.

## Status: what has and has not been run

| Part | State |
|---|---|
| Harness core, CLI, eval | Compiled and covered by 41 self-tests, including end-to-end runs against real git worktrees. |
| Sample app and the five planted failures | Checked in a real browser: the app is green, and each planted failure breaks exactly the scenarios it should. |
| Sample Reqnroll + Playwright suite (`sample/TinyShop.Tests`) | **Written but never compiled.** The environment it was built in could not reach NuGet. Run it once before relying on it; the package versions and the test names in `failures/manifest.json` are the likeliest things to need a tweak. |
| `dotnet test` runner and TRX parsing | The runner was exercised only as far as a failed restore. The parser is tested against a hand-written TRX file, not one produced by a real run. |
| Live model client | Request and response formats are unit-tested. It has not been called against a live endpoint. |

In the self-tests a simulator stands in for `dotnet test`, and scripted "models" stand in for a real one.

## Quick start

```sh
# 1. The harness's own tests. No packages, no browser, no API key.
dotnet run --project tests/FixHarness.SelfTests

# 2. The sample suite. Needs NuGet and a browser.
dotnet build sample/TinyShop.Tests
pwsh sample/TinyShop.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
dotnet test sample/TinyShop.Tests          # expect 4 passing scenarios

# 3. The whole harness against real "dotnet test", still with no API key:
#    scripted models replay known tool calls for each planted failure.
dotnet run --project src/FixHarness.Cli -- eval --model scripted:failures/scripts

# 4. One failure, watching a badly behaved "model" get blocked and corrected.
dotnet run --project src/FixHarness.Cli -- run --failure 01-renamed-locator --model scripted:failures/scripts --no-memory

# 5. A real model.
export FIXHARNESS_API_KEY=...
export FIXHARNESS_MODEL=...                 # e.g. an OpenRouter model id or an Azure deployment name
export FIXHARNESS_BASE_URL=...              # optional; defaults to https://openrouter.ai/api/v1
dotnet run --project src/FixHarness.Cli -- run --failure 03-async-total
dotnet run --project src/FixHarness.Cli -- eval
```

If step 3 reports `NoTestsMatched`, the test names differ from the filters in `failures/manifest.json`. `dotnet test sample/TinyShop.Tests --list-tests` shows the real names.

## One tag per step

Each step is a tagged commit, so `git diff step-2 step-3` shows exactly what a layer adds.

| Tag | Adds | What it fixes about the step before |
|---|---|---|
| `step-1` | Sample app, Reqnroll + Playwright suite, five planted failures | A problem with known answers to work on. |
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
 -> triage: a person's label, then rules               product defect: report and stop (no model)
 -> replay a remembered fix for this exact failure     verified: done (no model)
 -> agent loop: tools + guardrails + lessons from similar failures
 -> verify: diff obeys the policy, test passes 3 of 3, neighbours unbroken
      rejected: reset the workspace, tell the model why, try again
 -> remember what was learned; remove the workspace
```

The model is the last resort, not the first step.

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
| Rewrite what a `Then` step expects | That is how a product defect gets "fixed". A person decides what the product should do. Switch off with `AllowExpectationChanges`. |
| Touch more than 3 files or about 40 lines | A test repair is small. A big diff is something else. |

### Verifier (`Harness/Verifier.cs`)

Never reads the model's answer. It checks that something changed, that the diff obeys the policy, that the test passes three times running, and that no neighbouring test that was passing now fails. Zero tests run and skipped tests are never a pass.

### Triage (`Triage/`)

Rules run in order and the first verdict wins.

| Rule | Evidence | Verdict |
|---|---|---|
| `MissingStepBinding` | "No matching step definition" | Test issue |
| `BrowserScriptError` | The page threw an uncaught script error | Product defect |
| `ServerError` | A 5xx response | Product defect |
| `OnlyTestCodeChanged` | The preceding change touched only test code | Test issue |
| `BehaviourChangedByRefactor` | The change says "no behaviour change", product code changed, an assertion sees a new value | Product defect |
| `LocatorTimeoutAfterMarkupChange` | An element was not found and the change touched markup | Test issue |

A rule is one function; add your own through `FixHarnessRunner.TriageRules`.

When no rule matches, the model investigates and must end with `VERDICT: TEST_ISSUE` or `VERDICT: PRODUCT_DEFECT`. Its verdict is handled according to how much evidence there is:

- **Rules or a person say test issue, model says product defect:** the model is overruled, told why, and made to try again.
- **No rule matched, model says product defect:** accepted, but the report says it is a judgement for a person to confirm.

`BrowserScriptError` only works because the sample's hooks print browser errors into the test output (`sample/TinyShop.Tests/Support/Hooks.cs`). Without that, a crashed click handler and a stale locator both look like a timeout. It is worth copying into a real suite.

### Learning loop (`Memory/`)

What is learned lives in `.fixharness/memory.jsonl`: plain JSON lines you can read, review in a pull request and edit.

- **Replay.** A failure with the same fingerprint as one fixed before gets that patch applied and fully re-verified. If it passes, the run ends with no model call.
- **Lessons.** A similar failure (same shape, different test or selector) gets the earlier fix, or an earlier rejected attempt, as a hint in the model's context.
- **Labels.** `fixharness feedback --label test-issue` after a wrong classification. That exact failure is then classified your way every time, above the rules and the model.

Three rules keep it honest:

1. It learns only from evidence: fixes the verifier accepted, attempts the verifier rejected, labels a person gave. A model's opinion is never stored.
2. A remembered fix is never trusted on sight. Replay means the full verifier again.
3. It corrects itself. A remembered fix that fails verification twice stops being replayed.

Fingerprints are plain text processing (timings, line numbers, ids and paths stripped), so the same failure always gets the same one. There are no embeddings and no vector store.

### Eval (`Eval/`)

`fixharness eval` runs every planted failure and scores it against the known answer.

| Verdict | Meaning |
|---|---|
| `CorrectFix`, `CorrectDefect` | Right. |
| `MissedFix` | A test issue was not fixed. Costs time, harms nothing. |
| `DefectNotRecognised` | A defect produced no patch but was not reported either. Safe, not helpful. |
| `WronglyBlocked` | A test issue was reported as a product defect. Auto-fix stops and a false defect is raised. |
| `FalseFix` | A product defect was "fixed" in the test. A real bug is hidden. |

The command exits non-zero on `WronglyBlocked` or `FalseFix`, so it can gate a pipeline. Memory is off during an eval unless you pass `--with-memory`, so runs are comparable.

The five planted failures are a start, not a benchmark. The eval gets useful when you add failures from your own history.

## Using it on your own repository

1. Add `fixharness.json` at the repository root:

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
   fixharness run --filter "FullyQualifiedName~Basket.TotalAddsUp" --base origin/main --note "PR description here"
   ```

   `--base` gives the harness the diff that preceded the failure. It is the best evidence triage has and the most useful thing the model is shown.

Outputs go to `fixharness-out/`: `report.md` (ready for a pull-request comment or a defect ticket), `trace.json` (every tool call) and, only for a verified fix, `fix.patch`.

The harness works from the commit at `HEAD`. Uncommitted changes in your checkout are not seen.

## Limits

- A determined model can still write a wrong fix that passes: for example, hard-coding the expected value inside a step definition. The policy closes the common routes, not all of them. The output is a patch for review, never a merge.
- The triage rules are few and written for this sample. They will need extending for a real product, and `BehaviourChangedByRefactor` depends on what people write in pull-request descriptions.
- Only `dotnet test` is supported as a runner. `ITestRunner` is the seam for others.
- One test at a time, one workspace at a time. No parallel runs.
- The model sees source files and test output only. It has no DOM snapshot or Playwright trace tool yet, which matters for locator fixes in apps whose markup is not in the repository.

## Layout

```
src/FixHarness.Core/      the harness (no package references)
  Model/                  IModelClient, OpenAI-compatible client, scripted client
  Tools/                  the five tools
  Context/                what the model is shown; trimming
  Loop/                   the agent loop
  Guardrails/             loop guardrails, patch policy
  Harness/                workspace, runner, verifier, reports
  Triage/                 rules and verdicts
  Memory/                 fingerprints and the learning store
  Eval/                   the eval runner
  Testing/                dotnet test runner, TRX parser
src/FixHarness.Cli/       the fixharness command
tests/FixHarness.SelfTests/   tests for the harness (plain console app)
sample/TinyShop.Web/      the "product": one static page
sample/TinyShop.Tests/    Reqnroll + Playwright suite
failures/                 planted failures, their answers, scripted models
```
