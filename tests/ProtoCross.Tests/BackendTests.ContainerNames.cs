using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- a method named after the C# class it is declared in (spec 24.1)

    /// <summary>
    /// A method whose C# name would be its class's is declared and called with an underscore
    /// appended, beside its message as in a project: <c>ProtoCrossExtensions</c> for a project's
    /// class, and <c>TimestampProtoCrossExtensions</c> for <c>Timestamp</c>'s.
    /// </summary>
    [Theory]
    [InlineData("proto_cross_extensions", "acme.billing", "global::Acme.Billing.ProtoCrossExtensions", "ProtoCrossExtensions_")]
    [InlineData("timestamp_proto_cross_extensions", null, "global::Google.Protobuf.WellKnownTypes.TimestampProtoCrossExtensions", "TimestampProtoCrossExtensions_")]
    public void AMethodNamedAfterItsClassIsEscapedWhereverItIsNamed(string method, string? project, string container, string escaped)
    {
        var sources = TestPaths.WriteSources(
            TestPaths.CreateTempDirectory(),
            ("names.pcross", $$"""
                import proto "google/protobuf/timestamp.proto";

                extend google.protobuf.Timestamp {
                    fn {{method}}() -> int64 { return seconds + 1; }
                    fn caller() -> int64 { return {{method}}(); }
                }

                test google.protobuf.Timestamp.{{method}} "a container's name stays callable" {
                    receiver { seconds: 40 }
                    expect return 41;
                }
                """));
        var result = Compilation.Compile(sources, []) with
        {
            ProjectNamespace = project is null ? null : new ProjectNamespace(project),
        };
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var backend = new CSharpBackend();
        var diagnostics = new DiagnosticBag();
        var behavior = SourceEmission.Emit(result, backend, diagnostics).Single(file => file.RelativePath == "names.g.cs").Contents;
        var tests = SourceEmission.EmitTests(result, backend, diagnostics).Single().Contents;
        Assert.Empty(diagnostics);

        Assert.Contains($"public static long {escaped}(this ", behavior, StringComparison.Ordinal);
        Assert.Contains($"{container}.{escaped}(self)", behavior, StringComparison.Ordinal);
        Assert.Contains($"{container}.{escaped}(receiver)", tests, StringComparison.Ordinal);
    }
}
