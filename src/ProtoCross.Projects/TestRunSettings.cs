using System.Globalization;

namespace ProtoCross.Projects;

/// <summary>
/// What a project's <c>&lt;TestRun&gt;</c> chose about how <c>protocross test</c> runs its tests: how long
/// each step may take (spec 25.3.3). A project with no <c>&lt;TestRun&gt;</c> has <see cref="Default"/>.
/// </summary>
/// <remarks>
/// In the project rather than the configuration file for the reason <see cref="ProtoCrossProject.Scaffold"/>
/// is: it changes nothing a method means. How long a step may take depends on the repository's tests and
/// the machines they run on, and the project is what travels with the repository.
/// </remarks>
public sealed record TestRunSettings
{
    /// <summary>No setting stated, so each step may take <see cref="DefaultStepTimeout"/>.</summary>
    public static TestRunSettings Default { get; } = new();

    /// <summary>How long a step may take where no project says otherwise: 30 minutes.</summary>
    /// <remarks>
    /// <para>
    /// Long, because a first run restores packages and builds a test project from cold. The limit is for
    /// a step that has stopped, not one that is slow.
    /// </para>
    /// <para>
    /// Computed rather than stored, because <see cref="Default"/> reads it while this type's statics are
    /// being initialized, and a stored value declared after it would still be zero then.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultStepTimeout => TimeSpan.FromMinutes(30);

    /// <summary>The longest a project may let a step take: a day.</summary>
    /// <remarks>
    /// A step still running after a day has stopped, and a longer limit would only put off saying so. It
    /// also keeps every limit within what a process can be waited for, which is a little under 25 days.
    /// </remarks>
    public static TimeSpan LongestStepTimeout => TimeSpan.FromHours(24);

    /// <summary>What to write in place of a step timeout <see cref="TryParseStepTimeout"/> refuses.</summary>
    public const string StepTimeoutHelp =
        "Write a whole number followed by s, m or h, for seconds, minutes or hours, as '45m' is. It has to be "
            + "above zero and no more than 24h.";

    /// <summary>
    /// How long one step of a backend's run may take before it is taken to have hung and is stopped:
    /// <c>dotnet test</c>, and each of cmake's configure, its build and <c>ctest</c>.
    /// </summary>
    public TimeSpan StepTimeout { get; init; } = DefaultStepTimeout;

    /// <summary>
    /// Reads a step timeout as a project writes it: a whole number with no leading zero, then <c>s</c>,
    /// <c>m</c> or <c>h</c>, above zero and no more than <see cref="LongestStepTimeout"/>.
    /// </summary>
    /// <remarks>
    /// One spelling for each time, so <c>05m</c>, <c>45M</c> and <c>45 m</c> are refused rather than read
    /// as <c>45m</c>, as a scaffold setting is refused rather than rewritten. A number with no unit is
    /// refused too: <c>45</c> is minutes to one reader and seconds to the next.
    /// </remarks>
    public static bool TryParseStepTimeout(string value, out TimeSpan timeout)
    {
        timeout = default;

        // Six digits at most, so that the number fits an int before it is held to the longest limit.
        if (value is not [>= '1' and <= '9', .. var rest, var unit] || rest.Length > 5 || !rest.All(char.IsAsciiDigit))
        {
            return false;
        }

        var count = int.Parse(value[..^1], CultureInfo.InvariantCulture);
        TimeSpan? stated = unit switch
        {
            's' => TimeSpan.FromSeconds(count),
            'm' => TimeSpan.FromMinutes(count),
            'h' => TimeSpan.FromHours(count),
            _ => null,
        };

        if (stated is not { } time || time > LongestStepTimeout)
        {
            return false;
        }

        timeout = time;
        return true;
    }
}
