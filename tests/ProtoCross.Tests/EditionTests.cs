using Google.Protobuf.Reflection;
using ProtoCross.Binding;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Which protobuf editions a schema may be written in (spec 21.3).</summary>
/// <remarks>
/// Asked of descriptor sets built here rather than of protoc, because whether a protoc reaches the question
/// depends on its version: protoc 31.1 refuses edition 2024 itself, and protoc 35.1 compiles it and leaves the
/// refusal to the compiler.
/// </remarks>
public class EditionTests
{
    private static FileDescriptorSet SetOf(params FileDescriptorProto[] files)
    {
        var set = new FileDescriptorSet();
        set.File.AddRange(files);
        return set;
    }

    [Fact]
    public void ASchemaInAnEditionTooNewIsNamedBesideTheEditionsThatAreRead()
        => Assert.Equal(
            "'later.proto' is written in edition 2024, and ProtoCross reads proto2, proto3 and editions up to 2023.",
            DescriptorLoader.UnreadableEditionIn(SetOf(
                new FileDescriptorProto { Name = "earlier.proto", Syntax = "proto3" },
                new FileDescriptorProto { Name = "later.proto", Syntax = "editions", Edition = Edition._2024 })));

    [Fact]
    public void ProtoTwoProtoThreeAndEdition2023AreRead()
        => Assert.Null(DescriptorLoader.UnreadableEditionIn(SetOf(
            new FileDescriptorProto { Name = "two.proto", Syntax = "proto2" },
            new FileDescriptorProto { Name = "three.proto", Syntax = "proto3" },
            new FileDescriptorProto { Name = "edition.proto", Syntax = "editions", Edition = Edition._2023 })));
}
