namespace ProtoCross.Binding;

/// <summary>How a <c>protoc</c> executable came to be the one that will run.</summary>
/// <remarks>
/// The probe order in <see cref="ProtocLocator.Locate"/>, read back as data. It existed only as
/// control flow before: the locator knew perfectly well whether it had taken the environment
/// variable or fallen all the way through to a package cache, and then returned a bare string that
/// could not be asked. That is the first question a support request has to answer -- a user whose
/// <c>PROTOCROSS_PROTOC</c> has a typo in it sees a protoc found on <c>PATH</c> and no indication
/// that their setting was not the one used.
/// </remarks>
public enum ProtocSource
{
    /// <summary>A caller named this executable outright, so nothing was probed.</summary>
    Stated,

    /// <summary>The <c>PROTOCROSS_PROTOC</c> environment variable named it.</summary>
    EnvironmentVariable,

    /// <summary>It was found by searching <c>PATH</c>.</summary>
    SystemPath,

    /// <summary>It came from a Grpc.Tools package in a NuGet cache.</summary>
    NuGetPackage,

    /// <summary>Nothing named one and nothing was found.</summary>
    NotFound,
}

/// <summary>Which <c>protoc</c> will run, and what made it the answer.</summary>
/// <param name="Path">The executable, or null when none was found.</param>
/// <param name="Source">What produced it.</param>
public sealed record ProtocSelection(string? Path, ProtocSource Source)
{
    /// <summary>Nothing named a protoc and none could be found.</summary>
    public static ProtocSelection None { get; } = new(null, ProtocSource.NotFound);

    /// <summary>Whether there is an executable to run at all.</summary>
    public bool IsFound => Path is not null;

    /// <summary>How this source reads in a sentence, for a status report or a diagnostic.</summary>
    /// <remarks>
    /// Beside the enum rather than at the call site, for the reason <c>ConfigurationSources.Describe</c>
    /// is: a second reader wanting the same sentence is how one of the two stops matching the probe
    /// order.
    /// </remarks>
    public string Describe() => Source switch
    {
        ProtocSource.Stated => "named by a setting",
        ProtocSource.EnvironmentVariable => $"the {ProtocLocator.OverrideEnvironmentVariable} environment variable",
        ProtocSource.SystemPath => "found on PATH",
        ProtocSource.NuGetPackage => "a Grpc.Tools package in the NuGet cache",
        ProtocSource.NotFound => "not found",
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, "Unhandled protoc source."),
    };
}

/// <summary>What a <c>protoc</c> executable says it is, or why it would not say.</summary>
/// <param name="Reported">
/// protoc's own version line, trimmed, or null when it could not be asked.
/// </param>
/// <param name="Failure">Why it could not be asked, or null when it answered.</param>
/// <remarks>
/// <para>
/// Exactly one of the two is set, and both are kept because a report has to distinguish "protoc 25.1
/// is installed and is not the problem" from "the file at that path would not run". Collapsing them
/// into one string would put the reason for a failure where a reader is looking for a version number.
/// </para>
/// <para>
/// Asking is starting a process, so it is asked once per loader and remembered. The version of an
/// executable does not change underneath a running editor without the file being replaced, and a
/// replaced file is a different loader: the pool keys them by path.
/// </para>
/// </remarks>
public sealed record ProtocVersion(string? Reported, string? Failure)
{
    /// <summary>Whether protoc answered.</summary>
    public bool IsKnown => Reported is not null;

    /// <summary>The version, or the reason there is not one, as one line for a report.</summary>
    public string Describe() => Reported ?? $"unavailable ({Failure})";
}
