using System.Xml.Linq;

namespace TestAgentHarness.Testing;

/// <summary>Reads the .trx file that "dotnet test --logger trx" writes.</summary>
public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestCaseResult> Parse(string trxXml)
    {
        var document = XDocument.Parse(trxXml);
        var results = new List<TestCaseResult>();

        foreach (var result in document.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName") ?? "(unnamed)";
            var outcome = ((string?)result.Attribute("outcome") ?? "").ToLowerInvariant() switch
            {
                "passed" => TestOutcome.Passed,
                "failed" or "error" or "timeout" or "aborted" => TestOutcome.Failed,
                _ => TestOutcome.NotRun,
            };

            var errorInfo = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            var message = (string?)errorInfo?.Element(Ns + "Message");
            var stack = (string?)errorInfo?.Element(Ns + "StackTrace");

            // Keep what the test printed for tests that did not pass. Reqnroll puts
            // "no matching step definition" there, and the sample hooks print
            // browser console errors there.
            var stdOut = outcome == TestOutcome.Passed ? null : (string?)result.Element(Ns + "Output")?.Element(Ns + "StdOut");

            results.Add(new TestCaseResult(name, outcome, Shorten(message, 1500), Shorten(stack, 1200), Tail(stdOut, 1500)));
        }

        return results;
    }

    private static string? Tail(string? text, int max) =>
        text is null || text.Length <= max ? text : "..." + text[^max..];

    private static string? Shorten(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "...";
}
