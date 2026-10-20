using ProtoCross.Ir;
using ProtoCross.Semantics;

namespace ProtoCross.Backend.Cpp;

/// <summary>Operands evaluated left to right, where C++ would choose an order of its own (spec 9.3).</summary>
/// <remarks>
/// <para>
/// C++ fixes no order for a call's arguments, and none for the operands of most operators. The runtime's helpers, a
/// call and protobuf's accessors all put operands in an argument list, so <c>a + b</c> on integers is
/// <c>wrap_add_i64(a, b)</c>, and MSVC evaluates such a list right to left. The language evaluates every operand
/// left to right, and C# does.
/// </para>
/// <para>
/// Only where two operands may each not complete can anybody tell (<see cref="IrEvaluationOrder.IsObservable"/>).
/// There, the operands that may not complete are evaluated into locals first, in the order written, by a lambda
/// called where the expression is written, as a message literal is. Everywhere else the C++ is written as it always
/// was, so it moves only where the order could have been seen.
/// </para>
/// </remarks>
public sealed partial class CppBackend
{
    /// <summary>
    /// What <paramref name="build"/> makes of <paramref name="operands"/>, written as <paramref name="written"/>, with
    /// each operand that may not complete evaluated in the order written wherever that order can be seen.
    /// </summary>
    /// <param name="node">What the operands belong to. The lambda's names differ from every name it reads.</param>
    /// <param name="taken">Names <paramref name="build"/> writes that the lambda's must not be, such as a call's outs.</param>
    /// <remarks>
    /// <para>
    /// The operands that will complete are written where they stand. When they run beside the others cannot be
    /// seen, and leaving them in place keeps the C++ the shape it has everywhere else.
    /// </para>
    /// <para>
    /// Each local is a copy, <c>auto</c>, as every value this backend holds in a local is. A temporary is not copied
    /// at all, since C++17 builds it in place. A reference would be wrong here: a field read off what a lookup gave
    /// refers into a temporary that dies with the declaration, where written in place it lived to the end of the
    /// whole expression. The lambda captures by reference, and gives what the expression gives, by value, or nothing.
    /// </para>
    /// </remarks>
    private static string InOrder(
        IrNode node,
        IReadOnlyList<IrExpression> operands,
        IReadOnlyList<string> written,
        Func<IReadOnlyList<string>, string> build,
        Placement placement,
        params string[] taken)
    {
        if (!IrEvaluationOrder.IsObservable(operands))
        {
            return build(written);
        }

        var names = NamesFor(node, [.. OutNames(placement), .. taken]);
        var held = new List<string>(written.Count);
        var writer = new SourceWriter("  ");

        writer.WriteLine("[&] {");
        writer.Indent();
        for (var index = 0; index < operands.Count; index++)
        {
            if (!IrEvaluationOrder.MayNotComplete(operands[index]))
            {
                held.Add(written[index]);
                continue;
            }

            var name = names.Next("operand");
            writer.WriteLine($"auto {name} = {written[index]};");
            held.Add(name);
        }

        writer.WriteLine($"return {build(held)};");
        writer.Unindent();
        writer.Write("}()");
        return writer.ToString();
    }
}
