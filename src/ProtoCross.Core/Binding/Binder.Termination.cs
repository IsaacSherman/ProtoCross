using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

/// <summary>Whether a construct may end the program, which a project may forbid (spec 10.4, 19).</summary>
/// <remarks>
/// <para>
/// <c>protocross.config.xml</c> may say <c>Failure/Termination = Forbidden</c>, for code that runs inside a
/// process its authors do not own. Every construct that would end the program is then an error where it is
/// written (<c>PC0144</c>): each <c>fail</c> clause, and a conversion to a closed enum that says nothing. The
/// file reports the settings that would end it, checked overflow and a configured fallback of <c>fail</c>, as
/// it is read.
/// </para>
/// <para>
/// Refused rather than reinterpreted, so a source means one thing wherever it compiles. Where a construct is
/// refused, its help offers what may be written instead: a fallback, or a refusal from a method that declares
/// one (spec 16.3.1).
/// </para>
/// </remarks>
public sealed partial class Binder
{
    /// <summary>Whether this compilation's project forbids ending the program.</summary>
    private bool TerminationIsForbidden => _config.Termination is TerminationPolicy.Forbidden;

    /// <summary>
    /// Reports <paramref name="construct"/>, written at <paramref name="span"/>, where the project forbids
    /// ending the program (<c>PC0144</c>), with <paramref name="ways"/> to write instead as its help.
    /// </summary>
    private void ReportIfTerminationIsForbidden(string construct, SourceSpan span, IReadOnlyList<string> ways)
    {
        if (!TerminationIsForbidden)
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.TerminationForbidden,
            $"{construct} ends the program, which {ProjectConfig.TerminationKey} = {TerminationPolicy.Forbidden} in "
            + $"{ProjectConfig.FileName} forbids.",
            span,
            $"{Written(ways)} (spec 10.4, 16.3).");
    }

    /// <summary>
    /// The ways a clause, <paramref name="clause"/>, answers without ending the program: a value of
    /// <paramref name="fallback"/> where there is one to give, and a refusal.
    /// </summary>
    /// <param name="fallback">The type a fallback has, or null where the clause takes none.</param>
    /// <remarks>
    /// A refusal is offered as the method may write it: with its own type where it declares one, and with a
    /// declaration first where it does not. A test cannot refuse at all, so it is offered nothing.
    /// </remarks>
    private static List<string> WaysNotToEnd(string clause, PlType? fallback, MethodContext context)
    {
        List<string> ways = fallback is null || fallback is ErrorType ? [] : [$"'{clause}' and a '{fallback.DisplayName}' to use instead"];
        switch (context.Method)
        {
            case { Refusal: { } refusal } when refusal is not ErrorType:
                ways.Add($"'{clause} refuse' and a '{refusal.DisplayName}' to refuse");
                break;

            case { Refusal: null } method:
                ways.Add($"declare what '{method.Name}' refuses with and write '{clause} refuse' and one");
                break;
        }

        return ways;
    }

    /// <summary>Help that lists <paramref name="ways"/> to write: "Write a, b, or c".</summary>
    private static string Written(IReadOnlyList<string> ways) => ways.Count switch
    {
        0 => "Write a value instead",
        1 => $"Write {ways[0]}",
        _ => $"Write {string.Join(", ", ways.Take(ways.Count - 1))}, or {ways[^1]}",
    };
}
