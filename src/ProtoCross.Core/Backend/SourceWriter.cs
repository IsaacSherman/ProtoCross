using System.Text;

namespace ProtoCross.Backend;

/// <summary>
/// Minimal indentation-aware text writer. Generated output must be byte-for-byte deterministic so
/// golden tests stay meaningful, so this always emits "\n" regardless of host platform.
/// </summary>
public sealed class SourceWriter
{
    private const string NewLine = "\n";

    private readonly StringBuilder _builder = new();
    private readonly string _indentUnit;
    private int _indent;
    private bool _atLineStart = true;

    public SourceWriter(string indentUnit = "    ") => _indentUnit = indentUnit;

    public void Indent() => _indent++;

    public void Unindent() => _indent = Math.Max(0, _indent - 1);

    /// <summary>
    /// Writes <paramref name="text"/> at the current indentation. A line break in it starts a new
    /// line, indented as the first one was.
    /// </summary>
    /// <remarks>
    /// So a construct that spans lines can be written as text and then placed inside another. A C#
    /// message literal is an expression, and an expression is written into the line of the statement
    /// that holds it, at whatever depth that statement is. Each of the literal's lines is indented
    /// from that depth, exactly as if the statement's writer had written them one at a time, and
    /// whatever writes the literal never needs to know where it will land.
    /// </remarks>
    public void Write(string text)
    {
        // Found one break at a time rather than split, because nearly all text is one line, and
        // that line is then written without being copied.
        var start = 0;
        for (var lineBreak = text.IndexOf(NewLine, StringComparison.Ordinal);
             lineBreak >= 0;
             lineBreak = text.IndexOf(NewLine, start, StringComparison.Ordinal))
        {
            WriteWithinLine(text[start..lineBreak]);
            EndLine();
            start = lineBreak + NewLine.Length;
        }

        WriteWithinLine(text[start..]);
    }

    public void WriteLine(string text = "")
    {
        Write(text);
        EndLine();
    }

    /// <summary>Opens a brace-delimited block and indents until disposed.</summary>
    /// <param name="header">Text written before the opening brace, on its own line.</param>
    /// <param name="closer">
    /// Replacement for the default closing <c>}</c>, for trailing comments such as
    /// <c>}  // namespace foo</c>.
    /// </param>
    public IDisposable Block(string header, string? closer = null)
    {
        WriteLine(header);
        WriteLine("{");
        Indent();
        return new BlockScope(this, closer ?? "}");
    }

    public override string ToString() => _builder.ToString();

    private void WriteWithinLine(string text)
    {
        if (_atLineStart && text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(_indentUnit);
            }

            _atLineStart = false;
        }

        _builder.Append(text);
    }

    private void EndLine()
    {
        _builder.Append(NewLine);
        _atLineStart = true;
    }

    private sealed class BlockScope : IDisposable
    {
        private readonly SourceWriter _writer;
        private readonly string _closer;

        public BlockScope(SourceWriter writer, string closer)
        {
            _writer = writer;
            _closer = closer;
        }

        public void Dispose()
        {
            _writer.Unindent();
            _writer.WriteLine(_closer);
        }
    }
}
