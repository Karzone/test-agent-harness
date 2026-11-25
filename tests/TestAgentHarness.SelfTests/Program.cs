using TestAgentHarness.SelfTests;

// Usage: dotnet run --project tests/TestAgentHarness.SelfTests [-- <text a test name must contain>]
//        dotnet run --project tests/TestAgentHarness.SelfTests -- --report <output directory>

// Capability report: one end-to-end test per capability, written up as markdown and JSON.
if (args.Length == 2 && args[0] == "--report")
{
    return await CapabilityReport.RunAsync(Path.GetFullPath(args[1]));
}

var suite = new Suite();
CoreTests.Register(suite);
HarnessTests.Register(suite);
TriageTests.Register(suite);
MemoryTests.Register(suite);
EvalTests.Register(suite);
StoryTests.Register(suite);
BackendTests.Register(suite);

return await suite.RunAsync(args.FirstOrDefault());
