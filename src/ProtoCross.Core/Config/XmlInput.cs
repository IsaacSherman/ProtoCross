using System.Xml;
using System.Xml.Linq;
using ProtoCross.Diagnostics;

namespace ProtoCross.Config;

/// <summary>
/// An XML file the compiler takes settings from -- <c>protocross.config.xml</c>, or a project --
/// parsed with the positions its diagnostics are placed at.
/// </summary>
/// <remarks>
/// <para>
/// One home for reading such a file, because the configuration file and the project file are read
/// the same way and report the same failures the same way: a file that cannot be read, text that is
/// not XML, a root that is not the one expected. Two readers would come to place a position two
/// ways, and a diagnostic in one file would land a column away from the same diagnostic in the other.
/// </para>
/// <para>
/// The text is parsed from a copy this keeps rather than straight from the path, because a span
/// carries an absolute offset and XML line info does not. The cost is that an encoding named in the
/// XML declaration no longer overrides what the bytes say: <see cref="File.ReadAllText(string)"/>
/// follows a byte-order mark and otherwise reads UTF-8, which is what every such file is written in.
/// </para>
/// </remarks>
public sealed class XmlInput
{
    /// <summary>How every such file is parsed: with no document type definition.</summary>
    /// <remarks>
    /// Neither file has any use for one, and both come with a repository, which an editor reads the
    /// moment a folder is opened, trusted or not. A DTD is how a few hundred bytes of entity
    /// definitions expand into gigabytes of text, so one is refused as not being a file of either kind.
    /// </remarks>
    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit };

    private readonly string _text;
    private readonly LineMap _lines;

    private XmlInput(string name, string text, LineMap lines, XElement root)
    {
        Name = name;
        _text = text;
        _lines = lines;
        Root = root;
    }

    /// <summary>The name the file's diagnostics print: its file name, without the directory.</summary>
    public string Name { get; }

    /// <summary>The root element, whose name is the one <see cref="Read"/> was asked for.</summary>
    public XElement Root { get; }

    /// <summary>
    /// Reads <paramref name="path"/> and parses it, requiring a root element named
    /// <paramref name="rootName"/>.
    /// </summary>
    /// <returns>
    /// Null when the file cannot be read, is not XML, or has another root; the reason is reported as
    /// <paramref name="unreadable"/>, at the position the parser gave where it gave one.
    /// </returns>
    public static XmlInput? Read(
        string path,
        string rootName,
        DiagnosticDescriptor unreadable,
        DiagnosticBag diagnostics)
    {
        var name = System.IO.Path.GetFileName(path);

        string xml;
        try
        {
            xml = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Report(unreadable, ex.Message, new SourceSpan(name, SourcePosition.None, SourcePosition.None));
            return null;
        }

        var lines = new LineMap(xml);

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Report(unreadable, ex.Message, Span(name, lines, ex.LineNumber, ex.LinePosition));
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != rootName)
        {
            diagnostics.Report(
                unreadable,
                $"The root element must be <{rootName}>, not <{root?.Name.LocalName ?? "(empty)"}>.",
                Span(name, lines, root));
            return null;
        }

        return new XmlInput(name, xml, lines, root);
    }

    /// <summary>Where <paramref name="node"/> starts, or no position when the parser recorded none.</summary>
    public SourceSpan Span(XObject? node) => Span(Name, _lines, node);

    /// <summary>Where the first character of <paramref name="text"/> that is not whitespace is.</summary>
    /// <remarks>
    /// A text node starts where the markup before it ends, which is usually the end of a line, so a
    /// diagnostic about what the text says belongs where the saying starts. The whitespace is skipped in
    /// the file's own characters rather than in <see cref="XText.Value"/>, whose line endings the parser
    /// has already normalized.
    /// </remarks>
    public SourceSpan SpanOfText(XText text)
    {
        var start = Span(text);
        if (start.Start == SourcePosition.None)
        {
            return start;
        }

        var offset = start.Start.Offset;
        while (offset < _text.Length && char.IsWhiteSpace(_text[offset]))
        {
            offset++;
        }

        var position = _lines.PositionOf(offset);
        return new SourceSpan(Name, position, position);
    }

    private static SourceSpan Span(string name, LineMap lines, XObject? node)
        => node is IXmlLineInfo info && info.HasLineInfo()
            ? Span(name, lines, info.LineNumber, info.LinePosition)
            : Span(name, lines, 0, 0);

    /// <remarks>
    /// Zero-width, because it says where the problem is rather than how much of the file is wrong.
    /// Half-open ranges make that a legitimate empty range rather than a length nobody should read.
    /// A line of 0 is the XML parser saying it does not know, and stays out of band.
    /// </remarks>
    private static SourceSpan Span(string name, LineMap lines, int line, int column)
    {
        if (line <= 0)
        {
            return new SourceSpan(name, SourcePosition.None, SourcePosition.None);
        }

        var position = new SourcePosition(lines.OffsetOf(line, column), line, column);
        return new SourceSpan(name, position, position);
    }
}
