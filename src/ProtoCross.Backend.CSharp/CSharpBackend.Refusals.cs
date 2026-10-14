using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

/// <summary>Methods that may refuse, and calls to them (spec 16.3, 24.1).</summary>
/// <remarks>
/// <para>
/// A method that may refuse is declared <c>bool Create(…, out Invoice? result, out InvoiceRefusal? refusal)</c>.
/// It returns true and sets the result where it builds one, and false and sets the refusal where it refuses.
/// The other parameter is set to its default, as C# requires of every <c>out</c> parameter on every path.
/// </para>
/// <para>
/// A call written for its value tests what the method returned in a conditional, with the result in an
/// <c>out var</c> the method gives out (<see cref="Placement.Built"/>): <c>(Create(o, out var built, out _) ?
/// built : fallback)</c>. The fallback is evaluated only where the method refused, as the language orders it.
/// A call that passes a refusal on stands on its own (spec 16.3), so its statement tests it before anything
/// else, and leaves the method with the same refusal.
/// </para>
/// </remarks>
public sealed partial class CSharpBackend
{
    /// <summary>A call to a method that may refuse, written for its value: what it built, or what the clause gives instead.</summary>
    private static string EmitRefusableCall(IrRefusableCall refusable, Placement placement, string receiverName)
    {
        var built = placement.Built[refusable];
        var call = EmitCall(refusable.Call, placement, receiverName, $"out var {built}", "out _");
        var otherwise = refusable.OnRefused switch
        {
            RefusalBehavior.Fallback => Expression(refusable.Fallback!, placement, receiverName),
            RefusalBehavior.Fail
                => $"{CSharpRuntime.RefusalsTypeName}.Fail<{TypeName(refusable.Type)}>({FormatString(refusable.Call.Target.QualifiedName)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusable), refusable.OnRefused, "A refusal passed on or left unanswered is never a value a backend writes."),
        };

        return $"({call} ? {built} : {otherwise})";
    }

    /// <summary>A call to a method that may refuse, standing as a statement of its own: its value is not used.</summary>
    private static void EmitStandingRefusableCall(SourceWriter writer, IrRefusableCall refusable, Body body)
    {
        if (refusable.OnRefused is RefusalBehavior.Refuse)
        {
            EmitPassedOn(writer, refusable, body, keptIn: null);
            return;
        }

        var placement = body.Placement;
        using var refused = writer.Block($"if (!{EmitCall(refusable.Call, placement, ReceiverName, DiscardedOuts(refusable.Call.Target))})");
        writer.WriteLine(refusable.OnRefused switch
        {
            RefusalBehavior.Fail => $"{CSharpRuntime.RefusalsTypeName}.Fail({FormatString(refusable.Call.Target.QualifiedName)});",

            // Evaluated for what it does, as the language evaluates it where the method refused.
            RefusalBehavior.Fallback => $"_ = {Expression(refusable.Fallback!, placement)};",
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusable), refusable.OnRefused, "A refusal left unanswered never reaches a backend."),
        });
    }

    /// <summary>
    /// Tests a call that passes its method's refusal on, and leaves the method being written with it where it
    /// refused, before anything after it runs (spec 16.3).
    /// </summary>
    /// <param name="keptIn">The local the statement keeps what the call built in, or null where it does not use it.</param>
    /// <remarks>
    /// The refusal goes straight into the method's own <c>out</c> parameter, which the method being called sets as
    /// it refuses, so nothing copies it from one to the other.
    /// </remarks>
    private static void EmitPassedOn(SourceWriter writer, IrRefusableCall refusable, Body body, string? keptIn)
    {
        var outs = body.Outs!;
        string[] passed = refusable.Call.Type is VoidType
            ? [$"out {outs.Refusal}"]
            : [keptIn is null ? "out _" : $"out var {keptIn}", $"out {outs.Refusal}"];

        using (writer.Block($"if (!{EmitCall(refusable.Call, body.Placement, ReceiverName, passed)})"))
        {
            EmitLeaving(writer, outs);
        }
    }

    /// <summary>The value <paramref name="refusable"/> built, held in its local, as a local stores it.</summary>
    private static string BuiltValue(IrRefusableCall refusable, string built, Body body)
        => body.ForLocal(new HeldCopy(built, refusable.Type));

    /// <summary>A method that may refuse ending with its result: <c>return</c>, with or without a value.</summary>
    private static void EmitBuilt(SourceWriter writer, IrExpression? value, Outs outs, Placement placement)
    {
        if (value is not null)
        {
            writer.WriteLine($"{outs.Result} = {Expression(value, placement)};");
        }

        writer.WriteLine($"{outs.Refusal} = default;");
        writer.WriteLine("return true;");
    }

    /// <summary>A method that may refuse ending with its refusal: <c>refuse</c>.</summary>
    private static void EmitRefused(SourceWriter writer, IrRefuse refusal, Outs outs, Placement placement)
    {
        if (outs.Result is not null)
        {
            writer.WriteLine($"{outs.Result} = default;");
        }

        writer.WriteLine($"{outs.Refusal} = {Expression(refusal.Value, placement)};");
        writer.WriteLine("return false;");
    }

    /// <summary>Leaves a method that may refuse with the refusal already in its parameter.</summary>
    private static void EmitLeaving(SourceWriter writer, Outs outs)
    {
        if (outs.Result is not null)
        {
            writer.WriteLine($"{outs.Result} = default;");
        }

        writer.WriteLine("return false;");
    }

    /// <summary>The <c>out</c> arguments of a call to <paramref name="method"/> whose result and refusal are both unused.</summary>
    private static string[] DiscardedOuts(IrMethodSignature method) => method.ReturnType is VoidType ? ["out _"] : ["out _", "out _"];
}
