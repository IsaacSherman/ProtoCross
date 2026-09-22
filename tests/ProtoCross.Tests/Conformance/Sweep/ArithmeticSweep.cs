namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// The generated part of the conformance corpus: every numeric operator and conversion, over the
/// values where each can go wrong, under every overflow policy that governs it.
/// </summary>
/// <remarks>
/// <para>
/// The vectors are generated because nobody writes several thousand rows by hand without a mistake,
/// and a mistake in an expectation is a false alarm at best and a wrong answer written down as the
/// right one at worst. They are committed rather than generated as the tests run, because the
/// harness compiles whatever is in the vector directory, a failure should point at a line that
/// exists, and a file that exists can be opened in the editor.
/// </para>
/// <para>
/// <see cref="ArithmeticSweepTests"/> is what keeps the two in step: it fails when a committed file
/// differs from what this writes, and rewrites them when asked to.
/// </para>
/// </remarks>
internal static class ArithmeticSweep
{
    /// <summary>The directory, under <c>vectors/</c> or a policy directory in it, that holds each generated vector.</summary>
    public const string Directory = "sweep";

    public static IReadOnlyList<SweepVector> Vectors { get; } =
    [
        .. OverflowPolicy.All.Select(IntegerSweep.Render),
        FloatingSweep.Render(),
        ConversionSweep.Render(),
    ];

    public static IReadOnlyList<SweepFile> Files { get; } = [.. Vectors.SelectMany(vector => vector.Files())];
}
