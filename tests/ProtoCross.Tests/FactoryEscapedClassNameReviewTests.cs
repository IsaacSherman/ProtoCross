using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Factory names must be compared with the type names protoc actually declares.</summary>
public class FactoryEscapedClassNameReviewTests
{
    [Fact]
    public void ANestedTypeUnderAnEscapedParentDoesNotFalselyCollideWithAFactory()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "escaped_factory.proto"), """
            syntax = "proto3";
            package factory_review.escaped;
            message New {
                message Item { int64 number = 1; }
                message ItemFactory { int64 spare = 1; }
            }
            """);
        var source = Path.Combine(directory, "escaped_factory.pcross");
        File.WriteAllText(source, """
            import proto "escaped_factory.proto";
            factory Item {
                fn create() -> Item { return new Item { number: 3 }; }
            }
            """);
        var result = Compilation.Compile(source, [directory]);
        Assert.Null(result.SchemaFailure);
        var item = result.Types.FindMessage("factory_review.escaped.New.Item");
        var other = result.Types.FindMessage("factory_review.escaped.New.ItemFactory");
        Assert.NotNull(item);
        Assert.NotNull(other);
        Assert.Equal("New_ItemFactory", NameConventions.GetFactoryClassName(item!));
        Assert.Equal("New__ItemFactory", NameConventions.GetCppTypeName(other!));

        var protobuf = Toolchain.LocateProtobufCpp();
        Assert.NotNull(protobuf);
        Assert.NotNull(protobuf!.ProtocPath);
        var workspace = CppTestWorkspace.Create("factory-escaped-name-review");
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, directory, "escaped_factory.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var header = File.ReadAllText(Path.Combine(workspace.Directory, "escaped_factory.pb.h"));
        Assert.Contains("class New__ItemFactory final", header, StringComparison.Ordinal);

        Assert.True(result.Success,
            "The factory and the nested schema type have different names in both backends, so neither collides: "
            + string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.FactoryClassNameCollides.Code);
    }
}
