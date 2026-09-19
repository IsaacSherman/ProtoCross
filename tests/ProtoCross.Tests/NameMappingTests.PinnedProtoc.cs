using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Binding;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    /// <summary>
    /// What the Grpc.Tools protoc this repository pins generates from one schema, and the messages the
    /// schema declares.
    /// </summary>
    private static (IReadOnlyList<MessageDescriptor> Messages, string Generated) GenerateWithPinnedProtoc(
        string schemaName,
        string schema,
        string outputFlag,
        string generatedName)
    {
        var (files, directory) = GenerateWithPinnedProtoc([(schemaName, schema)], outputFlag);
        return ([.. files.Single().MessageTypes], File.ReadAllText(Path.Combine(directory, generatedName)));
    }

    /// <summary>
    /// Runs the Grpc.Tools protoc this repository pins once over several schemas, and returns the file
    /// each declares, in the order given, and the directory it generated into.
    /// </summary>
    /// <remarks>
    /// Never a protoc found on <c>PATH</c>. Each rule under test is a version's rule: protoc 21.x
    /// escapes a shorter C++ keyword list and no generated members, so an older system protoc would
    /// fail for a reason that is not a naming bug.
    /// </remarks>
    private static (IReadOnlyList<FileDescriptor> Files, string Directory) GenerateWithPinnedProtoc(
        IReadOnlyList<(string Name, string Text)> schemas,
        string outputFlag)
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        var directory = TestPaths.CreateTempDirectory();
        foreach (var (name, text) in schemas)
        {
            File.WriteAllText(Path.Combine(directory, name), text);
        }

        string[] names = [.. schemas.Select(schema => schema.Name)];
        var generated = Toolchain.RunProtoc(protoc, outputFlag, directory, directory, names);
        Assert.True(generated.ExitCode == 0, $"protoc --{outputFlag} failed.{Environment.NewLine}{generated.Output}");

        var descriptors = new DescriptorLoader(protoc).LoadBundle(names, [directory]).Descriptors;
        return ([.. names.Select(name => descriptors.Single(descriptor => descriptor.Name == name))], directory);
    }

    /// <summary>The protoc header for <paramref name="schemaText"/>, and the file it describes.</summary>
    /// <remarks>
    /// The file rather than only its messages, because the type and namespace checks below ask about
    /// everything a file declares -- its package and its top-level enums as well as its messages. The
    /// header is found by <see cref="NameConventions.GetCppProtoHeader"/>, the name the backend
    /// includes, so a check here cannot pass against a header the generated code would never see.
    /// </remarks>
    private static (FileDescriptor Schema, string Header) GenerateCppHeader(string schemaName, string schemaText)
    {
        var (files, directory) = GenerateWithPinnedProtoc([(schemaName, schemaText)], "cpp_out");
        var schema = files.Single();

        var header = File.ReadAllText(Path.Combine(directory, NameConventions.GetCppProtoHeader(schema)));
        return (schema, header.ReplaceLineEndings("\n"));
    }
}
