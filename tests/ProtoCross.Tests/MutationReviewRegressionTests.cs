using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Assignments preserve presence and evaluate their value before replacing the final field.</summary>
public class MutationReviewRegressionTests
{
    private static readonly string Schemas = Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript("import proto \"mutating_methods.proto\";\n" + source), [Schemas]);

    /// <summary>
    /// Reaching the target sets pending and unsets disputed before the value is read (spec 9.3).
    /// The earlier guard therefore cannot justify that read, even within the same assignment.
    /// </summary>
    [Theory]
    [InlineData("pending.cents")]
    [InlineData("pending.parent.cents")]
    public void WritingThroughAOneofMemberInvalidatesItsSiblingsGuardBeforeReadingTheValue(string target)
    {
        var source = $$"""
            extend Ledger {
                mut fn transfer() {
                    if has disputed {
                        {{target}} = disputed.cents;
                    }
                }
            }
            """;
        var result = Compile(source);
        Assert.NotNull(result.Module);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code != "PC0078");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PC0078");
        Assert.False(result.Success, "the target unsets the sibling before the guarded value can be read");
    }

    private static CompilationResult AssignmentCase(string scenario)
    {
        var source = scenario == "presence"
            ? """
              extend Ledger {
                  fn last_presence() -> int64 {
                      if has last { return 1; }
                      return 0;
                  }
                  mut fn replace() -> int64 {
                      last = new LedgerEntry { cents: last_presence() };
                      if has last { return last.cents; }
                      return -1;
                  }
              }
              test Ledger.replace "the assigned field stays unset until its value is evaluated" {
                  receiver { }
                  expect return 0;
              }
              """
            : """
              extend Ledger {
                  mut fn replace() -> int64 {
                      if has disputed {
                          pending = disputed;
                          if has pending { return pending.cents; }
                      }
                      return -1;
                  }
              }
              test Ledger.replace "copy a oneof sibling before replacing the case" {
                  receiver { disputed: new LedgerEntry { cents: 7 } }
                  expect return 7;
              }
              """;
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Single(result.EmittableModule!.Tests);
        return result;
    }

    private static IReadOnlyList<GeneratedFile> Generate(CompilationResult result, ITestBackend backend)
    {
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions("mutation_assignment_review.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return files;
    }

    [Theory]
    [InlineData("presence")]
    [InlineData("oneof")]
    public void CppEvaluatesTheValueBeforeSettingTheMessageFieldBeingAssigned(string scenario)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var result = AssignmentCase(scenario);
        var workspace = CppTestWorkspace.Create("mutation-assignment-review");
        workspace.Write(Generate(result, new CppBackend()));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, Schemas, "mutating_methods.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["mutation_assignment_review.tests.cc"], ["mutating_methods.pb.cc"]));
        Assert.True(run.Succeeded, "The final field is stored only after its value has been evaluated (spec 9.3)."
            + Environment.NewLine + run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }

    /// <summary>The same two assignments provide an executable C# control for the C++ regression.</summary>
    [Theory]
    [InlineData("presence")]
    [InlineData("oneof")]
    public void CSharpEvaluatesTheValueBeforeSettingTheMessageFieldBeingAssigned(string scenario)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var result = AssignmentCase(scenario);
        var workspace = CSharpTestWorkspace.Create("mutation-assignment-review-csharp");
        workspace.Write(Generate(result, new CSharpBackend()));
        var generated = workspace.GenerateProtobuf(protoc, Schemas, "mutating_methods.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
        Assert.True(Assert.Single(run.Executed).Passed, run.Process.Output);
    }
}
