using FixHarness.SelfTests;

// Usage: dotnet run --project tests/FixHarness.SelfTests [-- <text a test name must contain>]

var suite = new Suite();
CoreTests.Register(suite);
HarnessTests.Register(suite);
TriageTests.Register(suite);

return await suite.RunAsync(args.FirstOrDefault());
