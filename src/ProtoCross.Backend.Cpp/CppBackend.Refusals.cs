using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
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
/// <para>
/// A division, a conversion to an enum or a lookup whose clause refuses stands on its own too, and is tested the same
/// way (<see cref="EmitClauseRefusal"/>).
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

    /// <summary>
    /// Tests <paramref name="operation"/>, a division, a conversion to an enum or a lookup whose clause refuses and
    /// which stands in <paramref name="statement"/>, and leaves the method with <paramref name="refusal"/> where it
    /// gave no value, before the statement does anything (spec 16.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation goes through the runtime's helper that takes a fallback, as a lambda that marks the
    /// operation refused and gives a value nothing reads. What it gave is held in a local the statement then
    /// reads (<see cref="Placement.Held"/>), so nothing is stored, and no result is given, where it refused. The
    /// refusal is evaluated only there, as the language orders it.
    /// </para>
    /// <para>
    /// In a block of its own, so its locals are gone after it, under names nothing in the statement reads. A
    /// declaration's local is declared above the block, to stay in scope after it, and given the value inside it.
    /// </para>
    /// </remarks>
    private static void EmitClauseRefusal(
        SourceWriter writer, IrStatement statement, IrExpression operation, IrExpression refusal, Placement placement)
    {
        if (statement is IrVariableDeclaration declaration)
        {
            writer.WriteLine($"{TypeName(declaration.Local.Type)} {Escape(declaration.Local.Name)}{{}};");
            statement = new IrAssignment(
                new IrLocalReference(declaration.Local, declaration.Span), declaration.Initializer, declaration.Span);
        }

        using var scope = writer.Block(string.Empty);
        var names = NamesFor(statement, [.. OutNames(placement)]);
        var refused = names.Next("refused");
        var held = names.Next(IrRefusals.HeldNameOf(operation)!);

        var marked = $"[&] {{ {refused} = true; return {TypeName(operation.Type)}{{}}; }}";
        writer.WriteLine($"bool {refused} = false;");
        writer.WriteLine($"auto {held} = {operation switch
        {
            IrIntegerDivision division => DividedOr(division, marked, placement),
            IrNumberToEnum conversion => NamedOr(conversion, marked, placement),
            IrMapLookup lookup => ValueOr(lookup, marked, placement),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an operation a clause may refuse from."),
        }};");

        using (writer.Block($"if ({refused})"))
        {
            writer.WriteLine($"{placement.Outs!.Refusal} = {Expression(refusal, placement)};");
            writer.WriteLine("return false;");
        }

        var holding = new Dictionary<IrExpression, string>(ReferenceEqualityComparer.Instance) { [operation] = held };
        EmitStatement(writer, statement, placement with { Held = holding });
    }

    /// <summary>The references the method being written refuses through, which nothing declared inside it may shadow.</summary>
    private static IEnumerable<string> OutNames(Placement placement)
        => placement.Outs is { } outs ? new[] { outs.Result, outs.Refusal }.OfType<string>() : [];
}
