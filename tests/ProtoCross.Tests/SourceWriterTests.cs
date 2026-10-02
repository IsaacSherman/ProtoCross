using ProtoCross.Backend;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The writer every backend writes through. Text that spans lines is how a C# message literal
/// reaches it, inside whichever statement holds the literal.
/// </summary>
public class SourceWriterTests
{
    private static SourceWriter Indented()
    {
        var writer = new SourceWriter();
        writer.Indent();
        return writer;
    }

    [Fact]
    public void EveryLineOfTextThatSpansLinesIsIndentedAsTheFirstIs()
    {
        var writer = Indented();

        writer.WriteLine("first\nsecond");

        Assert.Equal("    first\n    second\n", writer.ToString());
    }

    /// <summary>
    /// Text that continues a line is not indented again where it joins that line, only on the lines
    /// it starts.
    /// </summary>
    [Fact]
    public void TextContinuingALineIndentsOnlyTheLinesItStarts()
    {
        var writer = Indented();

        writer.Write("return ");
        writer.WriteLine("new T\n{\n};");

        Assert.Equal("    return new T\n    {\n    };\n", writer.ToString());
    }

    [Fact]
    public void ABlankLineInsideTextIsLeftWithNoIndentation()
    {
        var writer = Indented();

        writer.WriteLine("first\n\nsecond");

        Assert.Equal("    first\n\n    second\n", writer.ToString());
    }
}
