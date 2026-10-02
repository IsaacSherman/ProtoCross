namespace ProtoCross.Tests;

internal static partial class CompiledCorpus
{
    /// <summary>
    /// Message literals everywhere one may be written but a fixture: in a method's statements and its
    /// conditions, as a call's argument, as a value inside another literal, given a list, and as a
    /// test's argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conformance vectors are what the sweeps are meant to sweep, and they cannot hold this yet: a
    /// vector is compiled and run by both backends, and the C++ backend does not generate a literal
    /// outside a fixture until #81. So the sweeps would meet a literal only in fixtures, where no name
    /// is in scope and a literal is never read from, and every question an editor asks about one in a
    /// method would go unasked. This entry asks them until a vector can.
    /// </para>
    /// <para>
    /// It compiles, so a sweep that applies an edit and recompiles has a clean starting point. Its
    /// values are worked out, so that when C++ catches up it can become a vector as it stands, and
    /// until then the C# smoke tests build it and run its tests.
    /// </para>
    /// </remarks>
    public const string LiteralsText =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn rebuilt(given: Inner) -> Outer {
                var made: Outer = new Outer {
                    status: TopLevelStatus.TOP_LEVEL_STATUS_OK,
                    inner: given,
                    nested_values: [Nested.NESTED_SOME, Nested.NESTED_NONE],
                    other_inner: new Inner { deep: Deep.DEEP_NONE },
                    count: count + 1,
                };

                return made;
            }

            fn counted() -> int64 {
                return new Outer { count: count }.count + rebuilt(new Inner { }).count;
            }

            fn labelled() -> bool {
                if new Outer { label: "x" }.label == label {
                    return true;
                }

                return false;
            }

            fn deep_of(given: Inner) -> Deep {
                return given.deep;
            }
        }

        test Outer.counted "a literal read from, and one passed" {
            receiver { count: 2 }
            expect return 5;
        }

        test Outer.labelled "a literal in a condition" {
            receiver { label: "x" }
            expect return true;
        }

        test Outer.deep_of "a message argument" {
            receiver { }
            arg given = new Inner { deep: Deep.DEEP_NONE };
            expect return Deep.DEEP_NONE;
        }
        """;

    /// <inheritdoc cref="LiteralsText"/>
    /// <remarks>
    /// Compiled in a type of its own, because <see cref="All"/> is initialized in another part of this
    /// class, and C# leaves the order of static initializers in two parts of one class unsaid. A
    /// nested type is initialized when it is first asked, whichever part asks.
    /// </remarks>
    public static CorpusSource Literals => LiteralsCompiled.Source;

    private static class LiteralsCompiled
    {
        public static CorpusSource Source { get; } = new(
            "method_literals",
            LiteralsText,
            Compilation.Compile(TestPaths.WriteTempScript(LiteralsText), [TestPaths.FixtureProtoDirectory]));
    }
}
