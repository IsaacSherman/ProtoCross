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
    // --- C++ field accessors ---
    //
    // protoc's C++ generator lowercases a field name and escapes it when it is a keyword or names a
    // member the message already has. keyword_fields.pcross runs that rule end to end in both
    // backends, for a few names. Every kind of name the rule treats differently is checked here
    // against the header protoc writes, where one schema holds them all and nothing has to be built.

    private const string AwkwardFieldSchemaName = "awkward_fields.proto";

    private const string AwkwardFieldSchema = """
        syntax = "proto3";

        package protocross.names;

        message AwkwardFields {
          int64 class = 1;
          int64 Friend = 2;
          int64 NULL = 3;
          int64 MixedCase = 4;
          int64 assert = 5;
          int64 and_eq = 6;
          int64 char8_t = 7;
          int64 descriptor = 8;
          int64 default_instance = 9;
          int64 unknown_fields = 10;
          int64 mutable_unknown_fields = 11;
          int64 swap = 12;
          int64 plain = 13;
        }

        message WithoutStandardDescriptor {
          option no_standard_descriptor_accessor = true;

          int64 descriptor = 1;
        }
        """;

    /// <summary>
    /// Every getter <see cref="NameConventions.GetCppFieldName"/> names is one protoc declared on that
    /// message, for a schema of the names its rule treats differently: keywords before and after
    /// lowercasing, a macro, an alternative token, generated members that are and are not nullary,
    /// and the one message that gives its <c>descriptor()</c> up.
    /// </summary>
    [Fact]
    public void EveryCppFieldNameIsAGetterTheProtocHeaderDeclares()
    {
        var (schema, header) = GenerateCppHeader(AwkwardFieldSchemaName, AwkwardFieldSchema);

        Assert.NotEmpty(schema.MessageTypes);
        foreach (var message in schema.MessageTypes)
        {
            var declarations = ClassBody(header, message.Name);
            foreach (var field in message.Fields.InDeclarationOrder())
            {
                var getter = $"::int64_t {NameConventions.GetCppFieldName(field)}() const;";
                Assert.True(
                    declarations.Contains(getter, StringComparison.Ordinal),
                    $"protoc declares no '{getter}' on {message.Name} for the field '{field.Name}'");
            }
        }
    }

    /// <summary>
    /// The declarations of one generated class, so a getter one message declares cannot answer for
    /// another: both messages here have a field called <c>descriptor</c>, spelled differently.
    /// </summary>
    private static string ClassBody(string header, string className)
    {
        var start = header.IndexOf($"class {className} final", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the generated header has no class {className}");

        var end = header.IndexOf("\n};", start, StringComparison.Ordinal);
        return header[start..end];
    }
}
