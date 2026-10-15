using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A leading-digit fix must preserve nested names whose enclosing name already makes them legal.</summary>
public class NestedNumericNamesReviewTests
{
    private static (CompilationResult Result, string Schemas) Compile()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "nested_numeric.proto"), """
            syntax = "proto3";
            package nested_numeric_review;
            message Outer {
                message _1Ticket { int64 number = 1; }
            }
            """);
        var source = Path.Combine(directory, "nested_numeric.pcross");
        File.WriteAllText(source, """
            import proto "nested_numeric.proto";
            factory nested_numeric_review.Outer._1Ticket {
                fn create() -> nested_numeric_review.Outer._1Ticket {
                    return new nested_numeric_review.Outer._1Ticket { number: 7 };
                }
            }
            extend nested_numeric_review.Outer._1Ticket {
                fn get_number() -> int64 { return number; }
            }
            """);
        var result = Compilation.Compile(source, [directory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return (result, directory);
    }

    [Fact]
    public void ANestedFactoryKeepsItsAlreadyLegalPublishedClassName()
    {
        var (result, _) = Compile();
        var message = Assert.Single(result.Module!.Factories).Message;
        Assert.Equal("Outer_1TicketFactory", NameConventions.GetFactoryClassName(message));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExistingCSharpConsumerOfANestedNumericNameStillBuilds(bool factory)
    {
        var (result, schemas) = Compile();
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        Assert.NotNull(dotnet);
        Assert.NotNull(protoc);
        var workspace = CSharpTestWorkspace.Create("nested-numeric-names-review");
        var generated = workspace.GenerateProtobuf(protoc, schemas, "nested_numeric.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var baseline = workspace.Build(dotnet);
        Assert.True(baseline.ExitCode == 0, baseline.Output);

        var diagnostics = new DiagnosticBag();
        workspace.Write(SourceEmission.Emit(result, new CSharpBackend(), diagnostics));
        Assert.Empty(diagnostics);
        var call = factory
            ? "global::NestedNumericReview.Outer_1TicketFactory.Create().Number"
            : "global::NestedNumericReview.Outer_1TicketProtoCrossExtensions.GetNumber(new global::NestedNumericReview.Outer.Types._1Ticket { Number = 7 })";
        File.WriteAllText(Path.Combine(workspace.Directory, "Consumer.cs"), $$"""
            public class Consumer {
                [Xunit.Fact]
                public void ThePublishedNameStillWorks() {
                    Xunit.Assert.Equal(7L, {{call}});
                }
            }
            """);
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
        Assert.Equal(1, run.PassedCount);
    }

    [Fact]
    public void AnExistingCppConsumerOfANestedNumericFactoryStillBuilds()
    {
        var (result, schemas) = Compile();
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a compiler and a linkable protobuf install.");
        }

        var workspace = CppTestWorkspace.Create("nested-numeric-names-review");
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "nested_numeric.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var driver = Path.Combine(workspace.Directory, "consumer.cc");
        File.WriteAllText(driver, "#include \"nested_numeric.pb.h\"\n");
        var baseline = workspace.RunSyntaxOnly(compiler, driver, protobuf.IncludeDirectory);
        Assert.True(baseline.ExitCode == 0, baseline.Output);

        var diagnostics = new DiagnosticBag();
        workspace.Write(SourceEmission.Emit(result, new CppBackend(), diagnostics));
        Assert.Empty(diagnostics);
        File.WriteAllText(driver, """
            #include "nested_numeric.pc.h"
            int main() {
                return nested_numeric_review::Outer_1TicketFactory::create().number() == 7 ? 0 : 1;
            }
            """);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf, ["consumer.cc"], ["nested_numeric.pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
    }
}
