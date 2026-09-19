using Google.Protobuf.Reflection;
using ProtoCross.Binding;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A protobuf extension is not a field of any message: not of the one it extends, and not of the one
/// whose scope declares it.
/// </summary>
/// <remarks>
/// <para>
/// The second half is the one that was wrong. Google.Protobuf answers
/// <c>MessageDescriptor.FindFieldByName</c> by looking the name up in the descriptor pool under the
/// message's full name, so an extension declared inside <c>Host</c> came back as though <c>Host</c>
/// declared it. The compiler accepted <c>scoped</c> in a <c>Host</c> method and emitted
/// <c>self.Scoped</c> and <c>self.scoped()</c>, and neither generated class has either: C# keeps the
/// extension on the nested static class <c>Host.Extensions</c>, and C++ keeps it as a static
/// identifier that is not an accessor.
/// </para>
/// <para>
/// Every door a name can come in by is swept rather than one sampled, because the binder looked
/// fields up in seven places, and a fix that reaches six of them leaves the seventh still finding
/// the extension.
/// </para>
/// </remarks>
public class ProtobufExtensionTests
{
    private const string Prelude = "import proto \"extensions.proto\";\n\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(Prelude + source), [TestPaths.FixtureProtoDirectory]);

    // ------- no written name reaches an extension

    /// <summary>
    /// Each door reports what it already reports for a name that is not a field, and nothing else:
    /// an extension is refused as unknown rather than as something new, because to the language it
    /// is exactly that.
    /// </summary>
    [Theory]
    [InlineData("a bare name in the declaring message",
        "extend Host { fn f() -> int64 { return scoped; } }", "PC0037")]
    [InlineData("a member of the declaring message",
        "extend Host { fn f(h: Host) -> int64 { return h.scoped; } }", "PC0041")]
    [InlineData("a bare presence test in the declaring message",
        "extend Host { fn f() -> bool { return has scoped; } }", "PC0041")]
    [InlineData("a presence test through the declaring message",
        "extend Host { fn f(h: Host) -> bool { return has h.scoped; } }", "PC0041")]
    [InlineData("a fixture of the declaring message",
        "extend Host { fn f() -> int64 { return held; } }\n"
        + "test Host.f \"sets an extension\" { receiver { scoped = 1; } expect return 0; }", "PC0059")]
    [InlineData("a bare name in the extended message",
        "extend Extendable { fn f() -> int64 { return scoped; } }", "PC0037")]
    [InlineData("a member of the extended message",
        "extend Host { fn f(e: Extendable) -> int64 { return e.scoped; } }", "PC0041")]
    [InlineData("a file-level extension in the extended message",
        "extend Extendable { fn f() -> int64 { return file_level; } }", "PC0037")]
    public void NoWrittenNameReachesAnExtension(string door, string source, string code)
    {
        var result = Compile(source);
        var codes = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToList();

        Assert.True(
            codes.SequenceEqual([code]),
            $"{door} must be reported as {code} and nothing else, but the compiler reported "
            + $"[{string.Join(", ", codes)}]");
        Assert.True(result.EmittableModule is null, $"{door} must not leave anything to emit");
    }

    // ------- an extension takes nothing from the message's name space

    /// <summary>
    /// A method may be called what an extension declared in its receiver is called. <c>PC0023</c>
    /// refused it, saying the receiver had a field of that name, which it does not; and the two
    /// generated names do not meet, which the conformance vector <c>extension_scope.pcross</c>
    /// proves by building and running it in both backends.
    /// </summary>
    [Fact]
    public void AMethodMayTakeTheNameOfAnExtensionItsReceiverDeclares()
    {
        var result = Compile("extend Host { fn scoped() -> int64 { return held; } fn f() -> int64 { return scoped(); } }");

        Assert.True(
            result.Success,
            "the only 'scoped' on Host is the method, so it must bind and be callable, but the compiler said: "
            + string.Join("; ", result.Diagnostics));
    }

    // ------- one answer to "which fields does this message have"

    /// <summary>
    /// The lookup and the listing are one answer. Every name declared anywhere in a message's scope
    /// -- its fields, the extensions it declares, its oneofs, its nested messages and enums -- is
    /// looked up, and is found exactly when the listing holds it, as the same descriptor. Swept over
    /// every message the fixture schemas declare, so the scope with an extension in it is one case
    /// among all of them rather than the only one anybody looked at.
    /// </summary>
    [Fact]
    public void ANameFindsAFieldExactlyWhenTheMessageListsIt()
    {
        var extensionsMet = 0;

        foreach (var message in EveryFixtureMessage())
        {
            var listed = MessageFields.InDeclarationOrder(message)
                .ToDictionary(field => field.Name, StringComparer.Ordinal);
            var extensions = message.Extensions.UnorderedExtensions.Select(extension => extension.Name).ToList();
            var declared = listed.Keys
                .Concat(extensions)
                .Concat(message.Oneofs.Select(oneof => oneof.Name))
                .Concat(message.NestedTypes.Select(nested => nested.Name))
                .Concat(message.EnumTypes.Select(nested => nested.Name));

            foreach (var name in declared)
            {
                Assert.True(
                    MessageFields.Named(message, name) == listed.GetValueOrDefault(name),
                    $"'{name}' in '{message.FullName}' must find a field exactly when the message lists "
                    + "that field");
            }

            extensionsMet += extensions.Count;
        }

        Assert.True(extensionsMet > 0, "the sweep must meet an extension declared inside a message");
    }

    // ------- helpers

    private static IEnumerable<MessageDescriptor> EveryFixtureMessage()
    {
        var imports = Directory.GetFiles(TestPaths.FixtureProtoDirectory, "*.proto")
            .Select(path => $"import proto \"{Path.GetFileName(path)}\";\n");
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(string.Concat(imports)),
            [TestPaths.FixtureProtoDirectory]);

        return result.Types.All.OfType<SchemaMessageName>().Select(message => message.Descriptor);
    }
}
