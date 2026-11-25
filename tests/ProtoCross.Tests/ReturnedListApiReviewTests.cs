using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>The caller owns a list it receives through the generated public API (spec 16.2).</summary>
public class ReturnedListApiReviewTests
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
    public void AReturnedListBelongsToTheCSharpCaller()
    {
        var dotnet = TestToolchain.RequireDotnet();
        var protoc = TestToolchain.RequireProtoc();
        var workspace = CSharpTestWorkspace.Create("returned-list-api-review");
        workspace.Write(Generate(new CSharpBackend()));
        var generated = workspace.GenerateProtobuf(protoc, ConformanceVectors.ProtoDirectory, Schema);
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        workspace.Write([new GeneratedFile("Consumer.cs", """
            using Protocross.Conformance;
            using Xunit;

            public class Consumer
            {
                [Theory]
                [InlineData(false)]
                [InlineData(true)]
                public void EditingTheResultDoesNotEditTheReceiver(bool refusable)
                {
                    var receiver = new ReturnedListReview { Items = { new ReturnedListItem { Amount = 5 } } };
                    var result = refusable ? Built(receiver) : receiver.Returned();

                    result[0].Amount = 9;
                    result.Add(new ReturnedListItem { Amount = 7 });

                    Assert.Equal(5L, receiver.Items[0].Amount);
                    Assert.Single(receiver.Items);
                }

                private static Google.Protobuf.Collections.RepeatedField<ReturnedListItem> Built(ReturnedListReview receiver)
                {
                    Assert.True(receiver.Built(out var result, out _));
                    return Assert.IsType<Google.Protobuf.Collections.RepeatedField<ReturnedListItem>>(result);
                }
            }
            """)]);

        var run = workspace.RunTests(dotnet);
        Assert.True(run.Executed.Count == 2, run.Process.Output);
        Assert.All(run.Executed, test => Assert.True(test.Passed, test.Name + ": " + test.Detail));
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }

    [Fact]
    public void AReturnedListBelongsToTheCppCaller()
    {
        TestToolchain.RequireCppCompiler();
        var protobuf = TestToolchain.RequireLinkableProtobufCpp();
        var workspace = CppTestWorkspace.Create("returned-list-api-review");
        workspace.Write(Generate(new CppBackend()));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, ConformanceVectors.ProtoDirectory, Schema);
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.Write([new GeneratedFile("consumer.cc", """
            #include "returned_list_review.pc.h"
            #include <iostream>

            int main() {
                using namespace protocross::conformance;
                for (bool refusable : {false, true}) {
                    ReturnedListReview receiver;
                    receiver.add_items()->set_amount(5);
                    google::protobuf::RepeatedPtrField<ReturnedListItem> result;
                    if (refusable) {
                        std::string refusal;
                        if (!built(receiver, result, refusal)) { return 2; }
                    } else {
                        result = returned(receiver);
                    }
                    result.Mutable(0)->set_amount(9);
                    result.Add()->set_amount(7);
                    if (receiver.items_size() != 1 || receiver.items(0).amount() != 5) {
                        std::cerr << "Editing the result changed the receiver (refusable=" << refusable << ")\n";
                        return 1;
                    }
                }
                return 0;
            }
            """)]);

        var run = Assert.Single(workspace.BuildAndRun(protobuf, ["consumer.cc"], [Vector + ".pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
    }
}
