using System.Globalization;
using System.Text.RegularExpressions;

namespace ProtoCross.Syntax;

/// <summary>What reading a numeric literal's spelling came to.</summary>
internal enum LiteralReading
{
    /// <summary>The spelling is a literal, and its value fits the widest type of its kind.</summary>
    Read,

    /// <summary>The spelling is no literal spec 6.6 allows.</summary>
    Malformed,

    /// <summary>The spelling is a literal, but no type of its kind can hold its value.</summary>
    OutOfRange,
}

/// <summary>
/// The spellings spec 6.6 allows for a numeric literal, and the value each one denotes.
/// </summary>
/// <remarks>
/// Separate from the lexer's scan on purpose. The scan decides where a literal ends, and decides it
/// generously -- everything glued to a number belongs to it -- so that a malformed spelling is one
/// token with one diagnostic. This decides whether what the scan found is a literal at all, from the
/// grammar written out once, below, rather than from the order a hand-written loop happened to check
/// things in.
/// </remarks>
internal static partial class NumericLiteralSpelling
{
    /// <summary>The spelling of positive infinity. Negative infinity is a negation of it.</summary>
    public const string Infinity = "__INF";

    /// <summary>The spelling of a NaN.</summary>
    public const string NotANumber = "__NAN";

    /// <summary>
    /// The value a name denotes if it is one of the two named floating-point literals, and null for
    /// any other name.
    /// </summary>
    public static FloatingPointValue? NamedValue(string name) => name switch
    {
        Infinity => FloatingPointValue.Infinity,
        NotANumber => FloatingPointValue.NaN,
        _ => null,
    };

    /// <summary>Reads an integer literal: decimal, or hexadecimal or binary after its prefix.</summary>
    public static LiteralReading ReadInteger(string text, out ulong magnitude)
    {
        magnitude = 0;

        var match = IntegerSpelling().Match(text);
        if (!match.Success)
        {
            return LiteralReading.Malformed;
        }

        var (digits, style) = match.Groups["hexadecimal"] is { Success: true } hexadecimal
            ? (hexadecimal.Value, NumberStyles.AllowHexSpecifier)
            : match.Groups["binary"] is { Success: true } binary
                ? (binary.Value, NumberStyles.AllowBinarySpecifier)
                : (match.Groups["decimal"].Value, NumberStyles.None);

        // The spelling is already known to be digits of the right base, so the only way left to fail
        // is a value past uint64 MAX.
        return ulong.TryParse(WithoutSeparators(digits), style, CultureInfo.InvariantCulture, out magnitude)
            ? LiteralReading.Read
            : LiteralReading.OutOfRange;
    }

    /// <summary>Reads a decimal floating-point literal: one with a fraction, an exponent, or both.</summary>
    public static LiteralReading ReadFloatingPoint(string text, out FloatingPointValue value)
    {
        value = default;

        if (!FloatingPointSpelling().IsMatch(text))
        {
            return LiteralReading.Malformed;
        }

        // A decimal too large for a double parses to an infinity rather than failing. The largest
        // double is the ceiling for every floating-point type, so that is the one range the literal
        // can be outside before anyone knows which type it will take.
        value = FloatingPointValue.Parse(WithoutSeparators(text));
        return double.IsInfinity(value.Double) ? LiteralReading.OutOfRange : LiteralReading.Read;
    }

    private static string WithoutSeparators(string digits) => digits.Replace("_", string.Empty, StringComparison.Ordinal);

    // A separator stands between two digits of the literal's own base and nowhere else, which rules out
    // one at either end, two together, and one beside the prefix, the point or the exponent. Digits are
    // ASCII: a character class range in .NET does not stretch to the other scripts' digits, which
    // char.IsDigit accepts and the lexer's scan therefore collects.
    [GeneratedRegex(
        @"\A(?:0x(?<hexadecimal>[0-9A-Fa-f]+(?:_[0-9A-Fa-f]+)*)|0b(?<binary>[01]+(?:_[01]+)*)|(?<decimal>[0-9]+(?:_[0-9]+)*))\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex IntegerSpelling();

    [GeneratedRegex(
        @"\A[0-9]+(?:_[0-9]+)*(?:\.[0-9]+(?:_[0-9]+)*)?(?:[eE][+-]?[0-9]+(?:_[0-9]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex FloatingPointSpelling();
}
