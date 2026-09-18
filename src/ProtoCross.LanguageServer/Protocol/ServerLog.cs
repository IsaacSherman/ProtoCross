namespace ProtoCross.LanguageServer.Protocol;

/// <summary>
/// How important a log line is. The numbers are LSP's <c>MessageType</c>, which is what goes on the
/// wire.
/// </summary>
public enum LogLevel
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Trace = 4,
}

/// <summary>Something the server logged as an error, and when.</summary>
/// <param name="When">Local time, because it is read beside an editor's own clock.</param>
/// <param name="Message">The line as it was logged, exception text and all.</param>
public sealed record LoggedError(DateTimeOffset When, string Message);

/// <summary>
/// Where the server says what it is doing, at a level the user can raise when reporting a problem.
/// </summary>
/// <remarks>
/// <para>
/// Two destinations, and both earn their place. <see cref="Sink"/> is wired to
/// <c>window/logMessage</c> once there is a connection, which is what puts the text in a channel the
/// user can open without leaving the editor. <see cref="Mirror"/> is standard error, which is where
/// anything that goes wrong <em>before</em> there is a connection has to go, and where a client that
/// captures the server's stderr will find it after a crash.
/// </para>
/// <para>
/// Standard output is not a destination and must never become one. It carries the protocol, and a
/// single stray line on it desynchronizes the framing for the rest of the session. <c>Program</c>
/// points <see cref="Console.Out"/> at standard error for that reason.
/// </para>
/// </remarks>
public sealed class ServerLog
{
    /// <summary>The least important level that is written. Raised by <c>$/setTrace</c>.</summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>The level the process was started with, which a client's <c>off</c> returns to.</summary>
    /// <remarks>
    /// Kept apart from <see cref="Level"/> because a client moves that one, and "go back to what I was
    /// started with" needs the answer from before it moved. <c>TraceLevel</c> says why <c>off</c> means
    /// this rather than errors only.
    /// </remarks>
    public LogLevel StartingLevel
    {
        get => _startingLevel;
        init
        {
            _startingLevel = value;
            Level = value;
        }
    }

    private readonly LogLevel _startingLevel = LogLevel.Info;

    /// <summary>Publishes to the client. Null until there is a connection to publish over.</summary>
    public Action<LogLevel, string>? Sink { get; set; }

    /// <summary>Where lines are mirrored regardless of the client. Never standard output.</summary>
    public TextWriter Mirror { get; init; } = Console.Error;

    /// <summary>The last thing that went wrong, or null if nothing has.</summary>
    /// <remarks>
    /// <para>
    /// Kept because a status report is opened after something went wrong, and by then the interesting
    /// line has scrolled out of an output channel the user may never have opened. A report that says
    /// "an exception at 14:02, here it is" turns "the extension is broken" into a defect report.
    /// </para>
    /// <para>
    /// <b>Errors only, and not warnings.</b> A warning here is routinely a setting being ignored,
    /// which is normal in a workspace that states nothing -- so the last warning would almost always
    /// be something harmless, and it would sit where a reader is looking for the failure. Those
    /// appear in the report anyway, against the document they are about, with the setting that caused
    /// them.
    /// </para>
    /// <para>
    /// Kept regardless of <see cref="Level"/>, for the same reason <see cref="Mirror"/> is written
    /// regardless of it: the level is a preference about what to show while working, and this is the
    /// record kept for a defect report.
    /// </para>
    /// </remarks>
    public LoggedError? LastError => Volatile.Read(ref _lastError);

    private LoggedError? _lastError;

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Trace(string message) => Write(LogLevel.Trace, message);

    /// <remarks>
    /// The mirror is written even when the level filters the client out, because the mirror is the
    /// record kept for a defect report and the level is a preference about what to show in an editor.
    /// Nothing here throws: a log that can fail a request is worse than no log.
    /// </remarks>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";

        if (level is LogLevel.Error)
        {
            Volatile.Write(ref _lastError, new LoggedError(DateTimeOffset.Now, text));
        }

        try
        {
            Mirror.WriteLine($"[{level.ToString().ToLowerInvariant()}] {text}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        if (level > Level)
        {
            return;
        }

        try
        {
            Sink?.Invoke(level, text);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
