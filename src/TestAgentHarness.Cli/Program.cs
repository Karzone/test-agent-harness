using TestAgentHarness.Cli;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine(Commands.Help);
    return 0;
}

try
{
    var options = new CliOptions(args.Skip(1));
    var repo = Path.GetFullPath(options.Get("repo") ?? Directory.GetCurrentDirectory());

    switch (args[0])
    {
        case "fix":
            return await Commands.RunAsync(options, repo);
        case "story":
            return await Commands.StoryAsync(options, repo);
        case "feedback":
            return Commands.Feedback(options, repo);
        case "memory":
            return Commands.ShowMemory(repo);
        case "eval":
            return await Commands.EvalAsync(options, repo);
        case "list":
            return Commands.List(repo);
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}\n\n{Commands.Help}");
            return 2;
    }
}
catch (Exception error) when (error is ArgumentException or FileNotFoundException or InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine($"Error: {error.Message}");
    return 2;
}
