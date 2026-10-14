using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

/// <summary>Methods that may refuse, and calls to them (spec 16.3, 24.2).</summary>
/// <remarks>
/// <para>
/// A method that may refuse is declared <c>static bool create(…, Invoice&amp; result, InvoiceRefusal&amp; refusal)</c>.
/// It returns true and sets the result where it builds one, and false and sets the refusal where it refuses, which
/// is the shape the C# backend gives it. The reference it does not set is left as it was handed in.
/// </para>
/// <para>
/// A call written for its value is a lambda called where it is written, as a message literal is, that declares
/// what the method builds and refuses with, calls it, and gives what it built or what the clause says instead.
/// The fallback is evaluated only where the method refused, as the language orders it. A call that passes a
/// refusal on stands on its own (spec 16.3), so its statement tests it before anything else, and leaves the method
/// with the same refusal.
/// </para>
/// </remarks>
public sealed partial class CppBackend
{
    /// <summary>A call to a method that may refuse, written for its value: what it built, or what the clause gives instead.</summary>
    /// <remarks>
    /// The names the lambda declares are none that the call's arguments or the fallback read, which are written
    /// inside it, and none of the references the method being written refuses through.
    /// </remarks>
    private static string EmitRefusableCall(IrRefusableCall refusable, Placement placement)
    {
        var names = NamesFor(refusable, [.. OutNames(placement)]);
        var builds = refusable.Type is not VoidType;
        var built = builds ? names.Next("built") : null;
        var refusal = names.Next("refusal");
        var writer = new SourceWriter("  ");

        writer.WriteLine(builds ? $"[&]() -> {TypeName(refusable.Type)} {{" : "[&] {");
        writer.Indent();
        if (built is not null)
        {
            writer.WriteLine($"{TypeName(refusable.Type)} {built}{{}};");
        }

        writer.WriteLine($"{TypeName(refusable.Call.Target.Refusal!)} {refusal}{{}};");
        using (writer.Block($"if (!{EmitCall(refusable.Call, placement, [.. new[] { built, refusal }.OfType<string>()])})"))
        {
            writer.WriteLine(refusable.OnRefused switch
            {
                RefusalBehavior.Fallback => $"return {Expression(refusable.Fallback!, placement)};",
                RefusalBehavior.Fail => $"{RuntimeNamespace}::refused({FormatString(refusable.Call.Target.QualifiedName)});",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(refusable), refusable.OnRefused, "A refusal passed on or left unanswered is never a value a backend writes."),
            });
        }

        if (built is not null)
        {
            writer.WriteLine($"return {built};");
        }

        writer.Unindent();
        writer.Write("}()");
        return writer.ToString();
    }

    /// <summary>
    /// Tests a call that passes its method's refusal on, and leaves the method being written with it where it
    /// refused, before anything after it runs (spec 16.3).
    /// </summary>
    /// <param name="result">What the call builds into, or null for a method that returns nothing.</param>
    /// <remarks>
    /// The refusal goes straight into the method's own reference, which the method being called sets as it
    /// refuses, so nothing copies it from one to the other.
    /// </remarks>
    private static void EmitPassedOn(SourceWriter writer, IrRefusableCall passed, string? result, Placement placement)
    {
        var refusal = placement.Outs!.Refusal;
        using var refused = writer.Block(
            $"if (!{EmitCall(passed.Call, placement, [.. new[] { result, refusal }.OfType<string>()])})");
        writer.WriteLine("return false;");
    }

    /// <summary>
    /// Passes a refusal on from a call whose result goes somewhere that already exists, or nowhere: into a local
    /// of its own first, so nothing is changed where the method refused.
    /// </summary>
    /// <param name="target">Where what the call built is stored, or null where it is not used.</param>
    /// <remarks>
    /// In a block of its own, so the local is gone after it, under a name nothing in the statement reads.
    /// </remarks>
    private static void EmitPassedOnInto(
        SourceWriter writer, IrRefusableCall passed, string? target, IrStatement statement, Placement placement)
    {
        using var scope = writer.Block(string.Empty);
        var built = NamesFor(statement, [.. OutNames(placement)]).Next("built");
        writer.WriteLine($"{TypeName(passed.Type)} {built}{{}};");
        EmitPassedOn(writer, passed, built, placement);
        if (target is not null)
        {
            writer.WriteLine($"{target} = {built};");
        }
    }

    /// <summary>The references the method being written refuses through, which nothing declared inside it may shadow.</summary>
    private static IEnumerable<string> OutNames(Placement placement)
        => placement.Outs is { } outs ? new[] { outs.Result, outs.Refusal }.OfType<string>() : [];
}
