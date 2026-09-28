using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Two libraries, each a project, that extend one message with a method of one name, used together
/// by one consumer (#29, spec 24).
/// </summary>
/// <remarks>
/// <para>
/// The message is <c>google.protobuf.Timestamp</c>, which neither owns: it is the case the issue was
/// filed for, and <c>PC0077</c> warns about it. Beside the message, each library would declare its
/// method in the runtime's namespace. In C# that is one class declared twice, which no consumer
/// referencing both can build. In C++ it is one <c>inline</c> function defined twice, which a single
/// translation unit refuses and two link together without a word, the linker keeping one body for
/// both.
/// </para>
/// <para>
/// Each library's files are generated into a directory of its own, as two libraries' would be, and
/// both have a source called <c>stamps.pcross</c>, so their headers share a name as well.
/// </para>
/// </remarks>
public class TwoLibrariesTests
{
    /// <summary>
    /// What the project named <paramref name="projectName"/> generates for a <c>stamps.pcross</c>
    /// whose <c>marker</c> is a timestamp's seconds plus <paramref name="offset"/>.
    /// </summary>
    private static IReadOnlyList<GeneratedFile> Library(IBackend backend, string projectName, int offset)
    {
        var sources = TestPaths.WriteSources(
            TestPaths.CreateTempDirectory(),
            ("stamps.pcross", $$"""
                import proto "google/protobuf/timestamp.proto";

                extend google.protobuf.Timestamp {
                    fn marker() -> int64 { return seconds + {{offset}}; }
                }
                """));
        var result = Compilation.Compile(sources, []) with { ProjectNamespace = new ProjectNamespace(projectName) };
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics);
        Assert.Empty(diagnostics);
        return files;
    }

    /// <summary><paramref name="files"/>, each under <paramref name="directory"/>.</summary>
    private static IEnumerable<GeneratedFile> Under(string directory, IEnumerable<GeneratedFile> files)
        => files.Select(file => file with { RelativePath = $"{directory}/{file.RelativePath}" });

    /// <summary>
    /// A C++ program can hold both libraries: one translation unit includes both headers and calls
    /// each library's function, and another calls the second library's alone. Every call returns
    /// its own library's answer, so neither the guards nor the linker merged the two.
    /// </summary>
    [Fact]
    public void TwoLibrariesExtendingOneMessageLinkIntoOneCppProgram()
    {
        var compiler = Toolchain.LocateCppCompiler();
        if (compiler is null)
        {
            Assert.Skip("No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Linking two libraries needs protobuf's C++ headers and libraries. Run 'vcpkg install'.");
        }

        var workspace = CppTestWorkspace.Create("two-libraries-cpp");
        workspace.Write(Under("alpha", Library(new CppBackend(), "alpha", 1)));
        workspace.Write(Under("beta", Library(new CppBackend(), "beta", 2)));
        workspace.Write(
        [
            new GeneratedFile("beta_elsewhere.cc", """
                #include <cstdint>
                #include "beta/stamps.pc.h"

                std::int64_t beta_marker_elsewhere(const ::google::protobuf::Timestamp& stamp) {
                  return ::beta::marker(stamp);
                }
                """),
            new GeneratedFile("main.cc", """
                #include <cstdint>
                #include <iostream>
                #include "alpha/stamps.pc.h"
                #include "beta/stamps.pc.h"

                std::int64_t beta_marker_elsewhere(const ::google::protobuf::Timestamp& stamp);

                int main() {
                  ::google::protobuf::Timestamp stamp;
                  stamp.set_seconds(40);
                  std::cout << "alpha " << ::alpha::marker(stamp) << ", beta " << ::beta::marker(stamp)
                            << ", beta elsewhere " << beta_marker_elsewhere(stamp) << std::endl;
                  return ::alpha::marker(stamp) == 41 && ::beta::marker(stamp) == 42
                      && beta_marker_elsewhere(stamp) == 42 ? 0 : 1;
                }
                """),
        ]);

        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf, ["main.cc"], ["beta_elsewhere.cc"]));

        Assert.True(
            run.Succeeded,
            $"Two libraries extending one message did not keep their own functions in one program.{Environment.NewLine}"
            + $"exit code {run.ExitCode}{Environment.NewLine}{run.Output}");
    }

    /// <summary>
    /// A C# assembly can hold both libraries, and a consumer chooses between them as the README says:
    /// the namespace it imports decides what an extension call reaches, and the class names the other.
    /// </summary>
    [Fact]
    public void TwoLibrariesExtendingOneMessageBuildIntoOneCSharpAssembly()
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var alpha = Library(new CSharpBackend(), "alpha", 1);
        var beta = Library(new CSharpBackend(), "beta", 2);

        // The arithmetic runtime is the same file in both, and an assembly takes one copy of it, as a
        // consumer building both libraries from source would.
        var runtime = alpha.Single(file => file.RelativePath == CSharpRuntime.FileName);
        var workspace = CSharpTestWorkspace.Create("two-libraries-csharp");
        workspace.Write(
        [
            runtime,
            .. Under("alpha", alpha.Where(file => file != runtime)),
            .. Under("beta", beta.Where(file => file.RelativePath != CSharpRuntime.FileName)),
            new GeneratedFile("Consumer.cs", """
                using Alpha;

                public sealed class Consumer
                {
                    [global::Xunit.Fact]
                    public void EachLibraryAnswersForItself()
                    {
                        var stamp = new global::Google.Protobuf.WellKnownTypes.Timestamp { Seconds = 40 };

                        global::Xunit.Assert.Equal(41L, stamp.Marker());
                        global::Xunit.Assert.Equal(42L, global::Beta.ProtoCrossExtensions.Marker(stamp));
                    }
                }
                """),
        ]);
        workspace.WriteProjectFiles();

        var run = workspace.RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"Two libraries extending one message did not build and run in one assembly.{Environment.NewLine}{run.Process.Output}");
        var executed = Assert.Single(run.Executed);
        Assert.True(executed.Passed, $"'{executed.Name}' was {executed.Outcome}: {executed.Detail}");
    }
}
