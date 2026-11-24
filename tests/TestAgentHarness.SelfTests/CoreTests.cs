using TestAgentHarness.Context;
using TestAgentHarness.Guardrails;
using TestAgentHarness.Loop;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;
using TestAgentHarness.Tools;

namespace TestAgentHarness.SelfTests;

/// <summary>Tests for the individual parts: results parsing, context, guardrails, tools, loop, model wire format.</summary>
public static class CoreTests
{
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Adding an item" outcome="Passed" />
            <UnitTestResult testName="Basket total adds up" outcome="Failed">
              <Output>
                <StdOut>[browser] pageerror: boom</StdOut>
                <ErrorInfo><Message>Expected: "£3.50" But was: "£0.00"</Message><StackTrace>at ShopSteps.cs:line 49</StackTrace></ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="Checking out" outcome="Inconclusive">
              <Output><StdOut>No matching step definition found</StdOut></Output>
            </UnitTestResult>
          </Results>
        </TestRun>
        """;

    public static void Register(Suite suite)
    {
        suite.Add("trx: parses passed, failed and inconclusive results", () =>
        {
            var tests = TrxParser.Parse(Trx);
            Check.Equal(3, tests.Count, "test count");
            Check.Equal(TestOutcome.Passed, tests[0].Outcome, "first outcome");
            Check.Equal(TestOutcome.Failed, tests[1].Outcome, "second outcome");
            Check.Contains("£0.00", tests[1].ErrorMessage!, "error message");
            Check.Contains("pageerror", tests[1].StdOut!, "stdout kept for failed test");
            Check.Equal(TestOutcome.NotRun, tests[2].Outcome, "inconclusive is not a pass");
            Check.True(!new TestRunResult(true, tests, "").AllPassed, "a run with failures is not AllPassed");
        });

        suite.Add("results: zero tests and skipped tests never count as a pass", () =>
        {
            Check.True(!new TestRunResult(true, [], "").AllPassed, "zero tests is not a pass");
            Check.True(new TestRunResult(true, [], "").NoTestsMatched, "zero tests is reported as no match");
            Check.True(!new TestRunResult(true, [new("t", TestOutcome.NotRun)], "").AllPassed, "skipped is not a pass");
            Check.True(!new TestRunResult(false, [], "build broke").AllPassed, "build failure is not a pass");
        });

        suite.Add("context: trimming keeps the task and never orphans a tool result", () =>
        {
            var messages = new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User("task") };
            for (var i = 0; i < 10; i++)
            {
                messages.Add(ChatMessage.Assistant(null, [new ToolCall($"a{i}", "t", "{}"), new ToolCall($"b{i}", "t", "{}")]));
                messages.Add(ChatMessage.ToolResult($"a{i}", "r"));
                messages.Add(ChatMessage.ToolResult($"b{i}", "r"));
            }

            Check.True(ContextTrimmer.Trim(messages, 12), "trimmed");
            Check.True(messages.Count <= 12, "within the limit");
            Check.Equal("system", messages[0].Role, "system prompt kept");
            Check.Equal("task", messages[1].Content, "task kept");
            Check.Equal("assistant", messages[2].Role, "first kept message after the task starts a turn");
            Check.True(!ContextTrimmer.Trim(messages, 12), "second trim is a no-op");
        });

        suite.Add("policy: allows a locator fix in test code", () =>
        {
            var violations = Policy().Check(Change("sample/TinyShop.Tests/Pages/ShopPage.cs",
                "x => page.Locator(\"[data-testid='basket-total']\");",
                "x => page.Locator(\"[data-testid='order-total']\");"));
            Check.Equal(0, violations.Count, "violations");
        });

        suite.Add("policy: rejects product code, removed or commented-out assertions, skips, sleeps, empty catches", () =>
        {
            const string test = "sample/TinyShop.Tests/Steps/ShopSteps.cs";
            Rejected(Change("sample/TinyShop.Web/app.js", "a", "b"), "not test code");
            Rejected(Change("../outside.cs", "a", "b"), "not test code");
            Rejected(Change(test, "Assert.That(a, Is.EqualTo(b));", ""), "removes an assertion");
            Rejected(Change(test, "Assert.That(a, Is.EqualTo(b));", "// Assert.That(a, Is.EqualTo(b));"), "removes an assertion");
            Rejected(Change(test, "await Expect(x).ToHaveTextAsync(y);", "await x.ClickAsync();"), "removes an assertion");
            Rejected(Change(test, "[Test]", "[Test]\n[Ignore(\"flaky\")]"), "skips or ignores");
            Rejected(Change(test, "Do();", "Assert.Inconclusive(); Do();"), "skips or ignores");
            Rejected(Change(test, "Do();", "Thread.Sleep(5000); Do();"), "fixed wait");
            Rejected(Change(test, "Do();", "await Task.Delay(5000); Do();"), "fixed wait");
            Rejected(Change(test, "Do();", "try { Do(); } catch { }"), "empty catch");
            Rejected(Change("sample/TinyShop.Tests/Features/Basket.feature", "  Scenario: A\n    Then x\n", "  @ignore\n  Scenario: A\n    Then x\n"), "skips or ignores");
            Rejected(Change("sample/TinyShop.Tests/Features/Basket.feature", "  Scenario: A\n    When y\n    Then x\n", "  Scenario: A\n    When y\n    # Then x\n"), "removes a");
        });

        suite.Add("policy: limits the size of a fix", () =>
        {
            var manyLines = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"var x{i} = {i};"));
            Rejected(Change("sample/TinyShop.Tests/Steps/ShopSteps.cs", "", manyLines), "lines; the limit");

            var manyFiles = Enumerable.Range(0, 4).ToDictionary(i => $"sample/TinyShop.Tests/F{i}.cs", _ => new FileChange("a", "b"));
            Check.True(Policy().Check(manyFiles).Any(v => v.Contains("files; the limit")), "file limit enforced");
        });

        suite.Add("tools: an edit needs one exact match and stays inside the workspace", async () =>
        {
            var directory = NewTempDirectory();
            File.WriteAllText(Path.Combine(directory, "a.cs"), "one\ntwo\ntwo\n");
            var tools = WorkspaceTools.Create(directory, new TinyShopSimulator(), "x");

            Check.Contains("not found", await Call(tools, "replace_in_file", new { path = "a.cs", old_text = "three", new_text = "3" }), "missing text");
            Check.Contains("appears 2 times", await Call(tools, "replace_in_file", new { path = "a.cs", old_text = "two", new_text = "2" }), "ambiguous text");
            Check.Contains("Edited a.cs", await Call(tools, "replace_in_file", new { path = "a.cs", old_text = "one", new_text = "1" }), "unique text");
            Check.Equal("1\ntwo\ntwo\n", File.ReadAllText(Path.Combine(directory, "a.cs")), "file content");
            Check.Contains("outside the workspace", await Call(tools, "read_file", new { path = "../../etc/passwd" }), "path escape on read");
            Check.Contains("outside the workspace", await Call(tools, "replace_in_file", new { path = "../x.cs", old_text = "a", new_text = "b" }), "path escape on edit");
            Check.Contains("Unknown tool", await tools.ExecuteAsync(new ToolCall("1", "delete_everything", "{}")), "unknown tool");
            Check.Contains("Error", await tools.ExecuteAsync(new ToolCall("1", "read_file", "not json")), "bad arguments do not crash the loop");
            Directory.Delete(directory, recursive: true);
        });

        suite.Add("tools: an edit rejected by the policy is not written", async () =>
        {
            var directory = NewTempDirectory();
            Directory.CreateDirectory(Path.Combine(directory, "tests"));
            Directory.CreateDirectory(Path.Combine(directory, "app"));
            File.WriteAllText(Path.Combine(directory, "tests", "t.cs"), "Assert.That(x, Is.True);\n");
            File.WriteAllText(Path.Combine(directory, "app", "p.js"), "let a = 1;\n");
            var tools = WorkspaceTools.Create(directory, new TinyShopSimulator(), "x", new PatchPolicy(new PatchPolicyOptions(["tests"])));

            Check.Contains("Rejected by guardrail", await Call(tools, "replace_in_file", new { path = "app/p.js", old_text = "1", new_text = "2" }), "product edit");
            Check.Equal("let a = 1;\n", File.ReadAllText(Path.Combine(directory, "app", "p.js")), "product file untouched");
            Check.Contains("Rejected by guardrail", await Call(tools, "replace_in_file", new { path = "tests/t.cs", old_text = "Assert.That(x, Is.True);", new_text = "" }), "assertion removal");
            Check.Equal("Assert.That(x, Is.True);\n", File.ReadAllText(Path.Combine(directory, "tests", "t.cs")), "test file untouched");
            Directory.Delete(directory, recursive: true);
        });

        suite.Add("loop: stops at the iteration limit", async () =>
        {
            var calls = 0;
            var tools = new ToolRegistry([new Tool(new ToolDefinition("tick", "", "{}"), (_, _) => Task.FromResult($"tick {++calls}"))]);
            var result = await AgentLoop.RunAsync(Forever("tick"), Task0(), tools, LoopGuardrails.MaxIterations(5));
            Check.Equal(StoppedBy.Guardrail, result.StoppedBy, "stopped by");
            Check.Equal(5, result.Iterations, "iterations");
            Check.Contains("iteration limit", result.Answer, "reason");
        });

        suite.Add("loop: detects a model that is stuck repeating itself", async () =>
        {
            var tools = new ToolRegistry([new Tool(new ToolDefinition("same", "", "{}"), (_, _) => Task.FromResult("same result"))]);
            var result = await AgentLoop.RunAsync(Forever("same"), Task0(), tools, LoopGuardrails.Default);
            Check.Equal(StoppedBy.Guardrail, result.StoppedBy, "stopped by");
            Check.Equal(3, result.Iterations, "stopped after three identical calls");
        });

        suite.Add("loop: long runs stay within the context limit", async () =>
        {
            var calls = 0;
            var largest = 0;
            var tools = new ToolRegistry([new Tool(new ToolDefinition("tick", "", "{}"), (_, _) => Task.FromResult($"tick {++calls}"))]);
            var result = await AgentLoop.RunAsync(Forever("tick"), Task0(), tools, LoopGuardrails.MaxIterations(40));
            foreach (var iteration in result.Trace) largest = Math.Max(largest, iteration.ContextSize);
            Check.True(largest <= 30, $"context never exceeded 30 messages (largest was {largest})");
            Check.True(result.Trace.Any(i => i.ContextTrimmed), "trimming happened");
        });

        suite.Add("model: request and response use the OpenAI chat-completions tool format", () =>
        {
            var request = OpenAiCompatibleClient.BuildRequest(
                "m",
                [ChatMessage.User("hi"), ChatMessage.Assistant(null, [new ToolCall("c1", "read_file", "{\"path\":\"a\"}")]), ChatMessage.ToolResult("c1", "content")],
                [new ToolDefinition("read_file", "Read", """{"type":"object","properties":{"path":{"type":"string"}}}""")]).ToJsonString();
            Check.Contains("\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\"", request, "assistant tool call");
            Check.Contains("\"role\":\"tool\",\"content\":\"content\",\"tool_call_id\":\"c1\"", request, "tool result");
            Check.Contains("\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"read_file\"", request, "tool definition");

            var response = OpenAiCompatibleClient.ParseResponse(
                """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"x","type":"function","function":{"name":"run_test","arguments":"{}"}}]}}]}""");
            Check.Equal(1, response.ToolCalls.Count, "tool calls parsed");
            Check.Equal("run_test", response.ToolCalls[0].Name, "tool name");

            var answer = OpenAiCompatibleClient.ParseResponse("""{"choices":[{"message":{"role":"assistant","content":"done"}}]}""");
            Check.Equal("done", answer.Content, "final answer parsed");
            Check.Equal(0, answer.ToolCalls.Count, "no tool calls on a final answer");
        });
    }

    private static PatchPolicy Policy() => new(new PatchPolicyOptions(["sample/TinyShop.Tests"]));

    private static Dictionary<string, FileChange> Change(string path, string before, string after) =>
        new() { [path] = new FileChange(before, after) };

    private static void Rejected(Dictionary<string, FileChange> changes, string expectedReason)
    {
        var violations = Policy().Check(changes);
        Check.True(violations.Any(v => v.Contains(expectedReason, StringComparison.OrdinalIgnoreCase)),
            $"a violation mentioning \"{expectedReason}\" for {changes.Keys.First()} (got: {string.Join(" | ", violations)})");
    }

    private static List<ChatMessage> Task0() => [ChatMessage.System("s"), ChatMessage.User("t")];

    private static ScriptedModelClient Forever(string tool) =>
        new([Enumerable.Repeat(new ScriptedModelClient.Turn(null, [(tool, "{}")]), 200).ToList()]);

    private static Task<string> Call(ToolRegistry tools, string name, object arguments) =>
        tools.ExecuteAsync(new ToolCall("call", name, System.Text.Json.JsonSerializer.Serialize(arguments)));

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tah-selftest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }
}
