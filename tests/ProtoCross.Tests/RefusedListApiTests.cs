using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The caller owns a list it receives as a refusal through the generated public API, as it owns one returned
/// (spec 16.3.1, 16.2).
/// </summary>
/// <remarks>
/// <c>returned_list_review</c>'s <c>refused</c> refuses with the receiver's own field. C++ assigns a refusal into
/// the caller's reference, which copies it; C# hands back a reference, so without a copy a caller's change to the
/// refusal would change the receiver. <see cref="ReturnedListApiReviewTests"/> holds a returned list to the same.
/// </remarks>
public class RefusedListApiTests
{
    private const string Vector = "returned_list_review";
    private const string Schema = Vector + ".proto";

    private static IReadOnlyList<GeneratedFile> Generate(IBackend backend)
    {
        var result = ConformanceVectors.Compile(ConformanceVectors.ByName(Vector));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics);
        Assert.Empty(diagnostics);
        return files;
    }

    [Fact]
    public void ARefusedListBelongsToTheCSharpCaller()
    {
        var dotnet = TestToolchain.RequireDotnet();
        var protoc = TestToolchain.RequireProtoc();
        var workspace = CSharpTestWorkspace.Create("refused-list-api");
        workspace.Write(Generate(new CSharpBackend()));
        var generated = workspace.GenerateProtobuf(protoc, ConformanceVectors.ProtoDirectory, Schema);
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        workspace.Write([new GeneratedFile("Consumer.cs", """
            using Protocross.Conformance;
            using Xunit;

            public class Consumer
            {
                [Fact]
                public void EditingTheRefusalDoesNotEditTheReceiver()
                {
                    var receiver = new ReturnedListReview { Items = { new ReturnedListItem { Amount = 5 } } };

                    Assert.False(receiver.Refused(out _, out var refusal));
                    refusal[0].Amount = 9;
                    refusal.Add(new ReturnedListItem { Amount = 7 });

                    Assert.Equal(5L, receiver.Items[0].Amount);
                    Assert.Single(receiver.Items);
                }
            }
            """)]);

        var run = workspace.RunTests(dotnet);
        Assert.True(run.Executed.Count == 1, run.Process.Output);
        Assert.All(run.Executed, test => Assert.True(test.Passed, test.Name + ": " + test.Detail));
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }

    [Fact]
    public void ARefusedListBelongsToTheCppCaller()
    {
        TestToolchain.RequireCppCompiler();
        var protobuf = TestToolchain.RequireLinkableProtobufCpp();
        var workspace = CppTestWorkspace.Create("refused-list-api");
        workspace.Write(Generate(new CppBackend()));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, ConformanceVectors.ProtoDirectory, Schema);
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.Write([new GeneratedFile("consumer.cc", """
            #include "returned_list_review.pc.h"
            #include <cstdint>
            #include <iostream>

            int main() {
                using namespace protocross::conformance;
                ReturnedListReview receiver;
                receiver.add_items()->set_amount(5);
                std::int64_t result = 0;
                google::protobuf::RepeatedPtrField<ReturnedListItem> refusal;
                if (refused(receiver, result, refusal)) { return 2; }
                refusal.Mutable(0)->set_amount(9);
                refusal.Add()->set_amount(7);
                if (receiver.items_size() != 1 || receiver.items(0).amount() != 5) {
                    std::cerr << "Editing the refusal changed the receiver\n";
                    return 1;
                }
                return 0;
            }
            """)]);

        var run = Assert.Single(workspace.BuildAndRun(protobuf, ["consumer.cc"], [Vector + ".pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
    }
}
