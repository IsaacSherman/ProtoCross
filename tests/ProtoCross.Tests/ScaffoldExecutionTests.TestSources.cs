using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class ScaffoldExecutionTests
{
    // ------- a test source's methods, generated with the tests (#106, spec 25.3.1)

    /// <summary>
    /// A test build whose test source declares a helper: the helper calls a production method, and
    /// a test targets each. The helper is generated into the test output and the method it calls
    /// into the behavior output, so this only builds if each build file finds the one from the other.
    /// </summary>
    private static CompilationResult TestBuildWithAHelper()
    {
        const string import = "import proto \"invoice.proto\";";

        return new Compilation(
                [
                    new SourceDocument(
                        SourceIdentity.Unsaved("pricing.pcross"),
                        $$"""
                          {{import}}

                          extend InvoiceItem {
                              fn gross() -> int64 { return quantity * unit_price_cents; }
                          }
                          """),
                    new SourceDocument(
                        SourceIdentity.Unsaved("pricing_checks.pcross"),
                        $$"""
                          {{import}}

                          extend InvoiceItem {
                              fn doubled() -> int64 { return gross() * 2; }
                          }

                          test InvoiceItem.gross "a production method" {
                              receiver { quantity = 2; unit_price_cents = 300; }
                              expect return 600;
                          }

                          test InvoiceItem.doubled "a helper calling a production method" {
                              receiver { quantity = 2; unit_price_cents = 300; }
                              expect return 1200;
                          }
                          """)
                    {
                        Role = SourceRole.Test,
                    },
                ],
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] })
            .Compile();
    }

    [Fact]
    public void TheEmittedCSharpProjectBuildsAndRunsTestsThatCallATestSource()
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var layout = ScaffoldLayout.Emit(new CSharpBackend(), "scaffold-csharp-helper", TestBuildWithAHelper());

        var run = CSharpTestWorkspace.At(layout.TestDirectory).RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"The scaffolded project failed to build or run.{Environment.NewLine}{run.Process.Output}");

        Assert.True(
            run.PassedCount == layout.ExpectedTestCount,
            $"Expected {layout.ExpectedTestCount} passing tests, saw {run.PassedCount?.ToString() ?? "none"}."
            + Environment.NewLine + run.Process.Output);
    }

    [Fact]
    public void TheEmittedCMakeProjectBuildsAndRunsTestsThatCallATestSource()
    {
        var cmake = Toolchain.LocateCMake();
        if (cmake is null)
        {
            Assert.Skip("No cmake found. Install CMake or Visual Studio's C++ workload.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOCROSS_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        if (Toolchain.LocateCppCompiler() is null)
        {
            Assert.Skip("No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        BuildAndRun(cmake, protobuf, ScaffoldLayout.Emit(new CppBackend(), "scaffold-cpp-helper", TestBuildWithAHelper()));
    }
}
