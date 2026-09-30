using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A project's generated container must not invalidate an otherwise legal method name.</summary>
public class ProjectNamespaceReviewRegressionTests
{
    private static IReadOnlyList<GeneratedFile> Generate(ITestBackend backend, bool asProject)
    {
        var sources = TestPaths.WriteSources(TestPaths.CreateTempDirectory(),
            ("names.pcross", """
                import proto "google/protobuf/timestamp.proto";

                extend google.protobuf.Timestamp {
                    fn proto_cross_extensions() -> int64 { return seconds + 1; }
                    fn caller() -> int64 { return proto_cross_extensions(); }
                }

                test google.protobuf.Timestamp.caller "a container name remains callable" {
                    receiver { seconds = 40; }
                    expect return 41;
                }
                """));
        var result = new Compilation([.. sources.Select(SourceDocument.ReadFrom)],
            new CompilationOptions
            {
                ProjectNamespace = asProject ? new ProjectNamespace("acme.billing") : null,
            }).Compile();
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics)
            .Concat(SourceEmission.EmitTests(result, backend, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return files;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMethodNamedAfterTheProjectContainerBuildsAndRunsInCSharp(bool asProject)
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }
        var workspace = CSharpTestWorkspace.Create("project-container-name");
        workspace.Write(Generate(new CSharpBackend(), asProject));
        workspace.WriteProjectFiles();

        var run = workspace.RunTests(dotnet);

        Assert.True(run.Process.ExitCode == 0,
            "A legal method must remain callable when emitted in a project's namespace."
            + Environment.NewLine + run.Process.Output);
        Assert.True(Assert.Single(run.Executed).Passed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMethodNamedAfterTheProjectContainerBuildsAndRunsInCpp(bool asProject)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }
        var workspace = CppTestWorkspace.Create("project-container-name");
        workspace.Write(Generate(new CppBackend(), asProject));
        workspace.Write([new GeneratedFile("anchor.cc", "#include <google/protobuf/timestamp.pb.h>\n")]);

        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf, ["names.tests.cc"], ["anchor.cc"]));

        Assert.True(run.Succeeded,
            "The same method and caller must build and run in C++." + Environment.NewLine + run.Output);
    }
}
