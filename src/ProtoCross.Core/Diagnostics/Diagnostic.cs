using System.Collections;
using System.Text;

namespace ProtoCross.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A single compiler message. The <paramref name="Code"/> is a <c>PC####</c> identifier as
/// described in spec 26; whether those codes are part of the compatibility contract is still
/// an open question, so treat them as stable-ish but not yet frozen.
/// </summary>
/// <remarks>
/// The code stays a <see cref="string"/> rather than becoming the
/// <see cref="DiagnosticDescriptor"/> it now comes from. Rendering is published output and so is the
/// code an editor client filters on, both of which want the text; and every consumer that compares a
/// code -- the host, the tests, the specification's own prose -- compares the string the spec quotes.
/// What the descriptor is for is the other direction: making sure two raise sites cannot disagree
/// about what a code means before it reaches here.
/// </remarks>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Message,
    SourceSpan Span,
    string? Help = null)
{
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Code).Append(": ").Append(Title).AppendLine();
        builder.Append(Span).AppendLine();
        builder.Append(Message);
        if (Help is not null)
        {
            builder.AppendLine();
            builder.Append("help: ").Append(Help);
        }

        return builder.ToString();
    }
}

public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>Reports one occurrence of a diagnostic: what happened here, and where.</summary>
    /// <remarks>
    /// The descriptor carries the code, the severity and the title, because those belong to the rule
    /// and must read the same wherever it is broken. The message belongs to the occurrence -- it
    /// names the type, the field or the count that made this one wrong -- and help does too, since
    /// the same rule can be repaired in different ways depending on how it was reached.
    /// </remarks>
    public void Report(DiagnosticDescriptor descriptor, string message, SourceSpan span, string? help = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Add(new Diagnostic(descriptor.Code, descriptor.Severity, descriptor.Title, message, span, help));
    }

    /// <summary>Reports a diagnostic from a code and title stated here rather than from a descriptor.</summary>
    /// <remarks>
    /// Kept for callers outside this repository: <c>DiagnosticBag</c> is part of the compiler's public
    /// surface and removing a method from it would break them for nothing. Nothing in <c>src/</c> uses
    /// either of these any more, and <c>DiagnosticCodeTests</c> fails if one comes back, because a code
    /// spelled at a raise site is a code that can disagree with the same code spelled somewhere else.
    /// </remarks>
    public void Error(string code, string title, string message, SourceSpan span, string? help = null)
        => Add(new Diagnostic(code, DiagnosticSeverity.Error, title, message, span, help));

    /// <inheritdoc cref="Error"/>
    public void Warning(string code, string title, string message, SourceSpan span, string? help = null)
        => Add(new Diagnostic(code, DiagnosticSeverity.Warning, title, message, span, help));

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
