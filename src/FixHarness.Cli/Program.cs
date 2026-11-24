using FixHarness.Cli;

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
        case "list":
            return Commands.List(repo);
        case "run":
            return await Commands.RunAsync(options, repo);
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}\n\n{Commands.Help}");
            return 2;
    }
}
catch (Exception error) when (error is ArgumentException or FileNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine($"Error: {error.Message}");
    return 2;
}
