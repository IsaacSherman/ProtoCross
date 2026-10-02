using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;

namespace ProtoCross.Backend;

/// <summary>
/// The message literals a backend refuses until it can generate them: every one but a test's fixture
/// (spec 13.2).
/// </summary>
/// <remarks>
/// <para>
/// A literal became an expression in #80's second step, and each backend learns to emit one in a
/// step of its own: C# learned in #80's third, and C++ learns in #81. Until then the C++ backend has
/// nothing to write for one, and spec 23 asks it to refuse what it does not support rather than emit
/// something that differs. A fixture is the exception, because both backends have always generated
/// fixtures, and its fields are the same nodes a literal's are.
/// </para>
/// <para>
/// So the fixture's own nesting is passed over -- its fields, the lists it gives, the literals it
/// gives them -- and everything else is searched: a method's body, a test's arguments and its
/// expectation, and any other expression inside a fixture's value. Only the outermost literal of
/// each is returned, because one diagnostic says what is wrong with the whole of it.
/// </para>
/// <para>
/// It lives here rather than in the C++ backend because both backends refused the same thing until
/// C# could generate it, and it goes when #81 lands. Methods and tests are asked separately because
/// a backend generates them separately, from one module: asking both of each would report a
/// method's literal twice.
/// </para>
/// </remarks>
public static class UngeneratedLiterals
{
    /// <summary>The outermost literals in <paramref name="module"/>'s methods, in source order.</summary>
    public static IReadOnlyList<IrMessageLiteral> InMethods(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return [.. module.Methods.SelectMany(Outermost)];
    }

    /// <summary>
    /// The outermost literals in <paramref name="module"/>'s tests, outside their fixtures, in source
    /// order.
    /// </summary>
    public static IReadOnlyList<IrMessageLiteral> InTests(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return
        [
            .. module.Tests.SelectMany(test => FixtureValues(test.Receiver)
                .Concat(test.Arguments)
                .Append<IrNode>(test.Expectation)
                .SelectMany(Outermost)),
        ];
    }

    /// <summary>
    /// Reports each of <paramref name="literals"/> as one <paramref name="backend"/> does not generate
    /// yet, and answers whether there was one, in which case the backend generates nothing at all.
    /// </summary>
    /// <param name="refusal">The backend's own code for it, from its range (spec 26).</param>
    /// <param name="backend">The language, as a reader names it: <c>C#</c>, <c>C++</c>.</param>
    /// <param name="until">When it will generate one, as the end of a sentence: <c>once #81 lands</c>.</param>
    /// <remarks>
    /// Nothing rather than everything but the literal, because a file missing one method is a file
    /// whose callers no longer compile, and the build that met it would report that instead of this.
    /// The words are here and not in each backend, so that the two say the same thing.
    /// </remarks>
    public static bool Refused(
        IReadOnlyList<IrMessageLiteral> literals,
        DiagnosticDescriptor refusal,
        string backend,
        string until,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(literals);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (var literal in literals)
        {
            diagnostics.Report(
                refusal,
                $"The {backend} backend does not generate a '{literal.MessageType.DisplayName}' literal here yet.",
                literal.Span,
                $"A literal is generated in a test's receiver fixture today, and everywhere else {until}.");
        }

        return literals.Count > 0;
    }

    /// <summary>
    /// What a fixture holds that is not more of the fixture: each value that is not a literal, and
    /// each element of a list that is not one either.
    /// </summary>
    private static IEnumerable<IrNode> FixtureValues(IrMessageLiteral fixture)
    {
        foreach (var field in fixture.Fields)
        {
            IEnumerable<IrExpression> values = field.Value is IrList list ? list.Elements : [field.Value];

            foreach (var value in values)
            {
                if (value is IrMessageLiteral nested)
                {
                    foreach (var inner in FixtureValues(nested))
                    {
                        yield return inner;
                    }
                }
                else
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>Each literal under <paramref name="node"/> that no other literal holds, in source order.</summary>
    private static IEnumerable<IrMessageLiteral> Outermost(IrNode node)
    {
        var pending = new Stack<IrNode>();
        pending.Push(node);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (current is IrMessageLiteral literal)
            {
                yield return literal;
                continue;
            }

            var children = IrWalk.ChildrenOf(current);
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }
}
