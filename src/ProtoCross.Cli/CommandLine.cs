namespace ProtoCross.Cli;

/// <summary>A verb the command line answers to.</summary>
/// <param name="Summary">What it does, as <c>--help</c> lists it.</param>
/// <param name="Run">Runs it over the arguments after its name, and returns the exit code.</param>
internal sealed record Verb(string Name, string Summary, Func<string[], int> Run);

/// <summary>
/// What <c>protocross</c> does with its arguments: runs the verb the first one names, or
/// <c>build</c> when it names none.
/// </summary>
/// <remarks>
/// <para>
/// A command line naming no verb is a <c>build</c>, so every one written before there were verbs still
/// means what it meant (#23).
/// </para>
/// <para>
/// A verb is recognized only as the first argument. Anywhere else <c>build</c> is a path like any other,
/// because a verb found further along would let a source's name decide what the command does. A source
/// named for a verb is written as a path to be read as one: <c>./build</c>.
/// </para>
/// <para>
/// <c>--version</c> is recognized only as the whole command line. Beside anything else it would be
/// a second request, and printing the version while quietly ignoring a source or a verb would look
/// like a build that did nothing. So <c>protocross --version x.pcross</c> is the unknown option it is
/// to <c>build</c>.
/// </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>The command line that asks for the version and nothing else.</summary>
    public const string VersionOption = "--version";

    /// <summary>What a command line naming no verb runs.</summary>
    private static readonly Verb Build = new("build", "Compile, and write the generated code (the default).", BuildCommand.Run);

    /// <summary>Every verb, in the order <c>--help</c> lists them.</summary>
    public static IReadOnlyList<Verb> All { get; } =
    [
        Build,
        new("test", "Build and run the generated tests in every backend, and judge them.", TestCommand.Run),
    ];

    public static int Run(string[] args)
        => args switch
        {
            [VersionOption] => PrintVersion(),
            [var first, .. var rest] when All.FirstOrDefault(verb => verb.Name == first) is { } named => named.Run(rest),
            _ => Build.Run(args),
        };

    /// <summary>
    /// Prints this build's version to standard output, as the server's status report states it, and
    /// succeeds.
    /// </summary>
    private static int PrintVersion()
    {
        Console.WriteLine(ProductVersion.Of(typeof(CommandLine)));
        return 0;
    }
}
