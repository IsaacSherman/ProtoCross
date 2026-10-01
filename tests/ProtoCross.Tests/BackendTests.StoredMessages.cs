using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- storing a message copies it (spec 13.2)

    /// <summary>
    /// A fixture whose message field is given a message that is not a literal: the result of a method
    /// that returns one of its receiver's own fields, which is exactly the message that would be shared
    /// if it were stored as it is.
    /// </summary>
    /// <remarks>
    /// A call is the only such value a test can write, having no names in scope, and calling a method
    /// from a fixture binds it against the receiver being built, so neither generated test compiles
    /// today. What is asserted is the copy, which is the backend's to spell however that is settled.
    /// </remarks>
    private const string StoredMessageSource =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn kept() -> Inner {
                while true {
                    if has other_inner {
                        return other_inner;
                    }
                }
            }

            fn f() -> int64 { return count; }
        }

        test Outer.f "a stored message" {
            receiver { inner: kept() }
            expect return 0;
        }
        """;

    /// <summary>The generated test for <see cref="StoredMessageSource"/> in one backend.</summary>
    private static string StoredMessageTest(string backendName)
    {
        var result = Compilation.Compile(TestPaths.WriteTempScript(StoredMessageSource), [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var file = Assert.Single(SourceEmission.EmitTests(result, BackendNamed(backendName), diagnostics));

        Assert.Empty(diagnostics);
        return file.Contents;
    }

    /// <summary>A C# message is a reference, so a stored one that is not a literal is cloned.</summary>
    [Fact]
    public void ACSharpFixtureClonesAStoredMessageThatIsNotALiteral()
        => Assert.Matches(@"Inner = [^\n]*Kept\([^\n]*\)\.Clone\(\),", StoredMessageTest("csharp"));

    /// <summary>
    /// A C++ message has no setter. It is stored by assigning to the one the field holds, which copies
    /// it.
    /// </summary>
    [Fact]
    public void ACppFixtureAssignsAStoredMessageToTheFieldsOwn()
        => Assert.Contains("*receiver.mutable_inner() = ", StoredMessageTest("cpp"), StringComparison.Ordinal);
}
