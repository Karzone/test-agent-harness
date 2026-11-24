using TestAgentHarness.SelfTests;

// Usage: dotnet run --project tests/TestAgentHarness.SelfTests [-- <text a test name must contain>]

var suite = new Suite();
CoreTests.Register(suite);
HarnessTests.Register(suite);
TriageTests.Register(suite);
MemoryTests.Register(suite);
EvalTests.Register(suite);

return await suite.RunAsync(args.FirstOrDefault());
