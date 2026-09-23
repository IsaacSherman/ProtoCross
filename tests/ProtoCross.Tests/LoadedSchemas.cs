using Google.Protobuf.Reflection;
using ProtoCross.Binding;
using ProtoCross.Tests.Conformance;

namespace ProtoCross.Tests;

/// <summary>
/// Descriptors loaded once for the whole assembly, for the tests that bind trees directly rather than
/// compiling a source.
/// </summary>
internal static class LoadedSchemas
{
    private static readonly Lazy<IReadOnlyList<FileDescriptor>> Loaded = new(()
        => DescriptorLoader.CreateDefault().Load(
            ["invoice.proto", .. ConformanceVectors.SchemaFileNames],
            [TestPaths.ExampleProtoDirectory, ConformanceVectors.ProtoDirectory]));

    /// <summary>The example schema and every conformance schema, from one protoc run.</summary>
    /// <remarks>
    /// One run rather than one per schema, because a binder resolves against one set of descriptors,
    /// and two loads of one schema are two different objects standing for the same message. Once for
    /// the assembly, because the resilience sweeps bind tens of thousands of trees and must not pay
    /// for a process each.
    /// </remarks>
    public static IReadOnlyList<FileDescriptor> ExampleAndConformance => Loaded.Value;
}
