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
    // --- C++ type, enum value, and namespace names ---
    //
    // protoc's C++ generator escapes a type's flattened name against the keywords and the members a
    // generated message declares, each enum value against the keywords, and each package component
    // the same way. keyword_types.pcross and keyword_package.pcross run that end to end, but a
    // vector has to compile in C# too, and a nested message called MergeFrom does not: C# forbids a
    // member named after its enclosing class. So the whole of each rule is checked here, against
    // the header protoc writes.

    private const string AwkwardTypeSchemaName = "awkward_types.proto";

    private const string AwkwardTypeSchema = """
        syntax = "proto3";

        // A keyword, a macro, and a generated member's name, which is escaped as a class and not as
        // a package.
        package awkward.new.NULL.Swap;

        message GetDescriptor {}
        message GetReflection {}
        message default_instance {}
        message Swap {}
        message UnsafeArenaSwap {}
        message CopyFrom {}
        message MergeFrom {}
        message IsInitialized {}
        message GetMetadata {}
        message union {}

        message New {
          message Inner {}
          message MergeFrom {}

          enum Kind {
            class = 0;
            KIND_PLAIN = 1;
          }
        }

        message Plain {
          message Clear {}
        }

        message thread {
          message local {}
        }

        message wchar {
          enum t {
            T_ZERO = 0;
          }
        }

        enum Clear {
          new = 0;
          delete = 1;
          NULL = 2;
          assert = 3;
          not_eq = 4;
          PLAIN = 5;
        }

        enum friend {
          FRIEND_ZERO = 0;
        }
        """;

    /// <summary>
    /// Every class name <see cref="NameConventions.GetCppTypeName(MessageDescriptor)"/> gives, and
    /// every enum name <see cref="NameConventions.GetCppTypeName(EnumDescriptor)"/> gives, is one the
    /// protoc header declares, for a schema of every generated member a class could collide with, a
    /// keyword, a nested type under an escaped parent, nested names that collide only once flattened
    /// and nested names that stop colliding once flattened.
    /// </summary>
    [Fact]
    public void EveryCppTypeNameIsAClassOrEnumTheProtocHeaderDeclares()
    {
        var (schema, header) = GenerateCppHeader(AwkwardTypeSchemaName, AwkwardTypeSchema);

        var messages = AllMessages(schema).ToList();
        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            var declaration = $"\nclass {NameConventions.GetCppTypeName(message)} final ";
            Assert.True(
                header.Contains(declaration, StringComparison.Ordinal),
                $"protoc declares no '{declaration.Trim()}' for the message '{message.FullName}'");
        }

        var enums = AllEnums(schema).ToList();
        Assert.NotEmpty(enums);
        foreach (var enumType in enums)
        {
            var declaration = $"\nenum {NameConventions.GetCppTypeName(enumType)} : int {{";
            Assert.True(
                header.Contains(declaration, StringComparison.Ordinal),
                $"protoc declares no '{declaration.Trim()}' for the enum '{enumType.FullName}'");
        }
    }

    /// <summary>
    /// Every enum value name <see cref="NameConventions.GetCppValueName"/> gives is a constant the
    /// protoc header defines at namespace scope, with that value's number: keywords, a macro and an
    /// alternative token in a top-level enum, and a keyword in an enum nested under an escaped class.
    /// </summary>
    [Fact]
    public void EveryCppValueNameIsAConstantTheProtocHeaderDefines()
    {
        var (schema, header) = GenerateCppHeader(AwkwardTypeSchemaName, AwkwardTypeSchema);

        var values = AllEnums(schema).SelectMany(enumType => enumType.Values).ToList();
        Assert.NotEmpty(values);
        foreach (var value in values)
        {
            var definition = $"\n  {NameConventions.GetCppValueName(value)} = {value.Number},\n";
            Assert.True(
                header.Contains(definition, StringComparison.Ordinal),
                $"protoc defines no '{definition.Trim()}' for the value '{value.FullName}'");
        }
    }

    /// <summary>
    /// The names <see cref="NameConventions.GetCppEnumCompanionNames"/> gives an enum, with its class and its
    /// values, are every name the protoc header declares at namespace scope under the enum's names, and no
    /// others: for
    /// an open enum and a closed one, each top-level, nested and nested twice, a class escaped, a largest
    /// value of <c>int.MaxValue</c>, a file built for the lite runtime, and an editions enum of each kind.
    /// </summary>
    /// <remarks>
    /// Read off the header rather than listed here, so a name protoc adds or drops fails it. A name at
    /// namespace scope is on a line protoc does not indent, or is an enumerator; one in a message's class
    /// is indented.
    /// </remarks>
    [Fact]
    public void EveryNameProtocDeclaresBesideAnEnumIsACompanionOrAValue()
    {
        var (files, directory) = GenerateWithPinnedProtoc(
            [
                ("companions_open.proto", """
                    syntax = "proto3";
                    package companions.open;
                    enum Kind { READY = 0; DONE = 5; }
                    enum Swap { SWAP_A = 0; }
                    enum Huge { HUGE_ZERO = 0; HUGE_MAX = 2147483647; }
                    message Outer {
                      enum Level { LOW = 0; HIGH = 2; }
                      message Inner { enum Deep { DEEP_A = 0; } }
                    }
                    message New { enum Kind { NEW_KIND_A = 0; } }
                    """),
                ("companions_closed.proto", """
                    syntax = "proto2";
                    package companions.closed;
                    enum Shade { DARK = 1; LIGHT = 3; }
                    enum Big { BIG_LOW = -2147483648; BIG_HIGH = 2147483647; }
                    message Box { enum Size { SMALL = 1; } }
                    """),
                ("companions_lite.proto", """
                    syntax = "proto3";
                    package companions.lite;
                    option optimize_for = LITE_RUNTIME;
                    enum Tone { TONE_A = 0; }
                    message Holder { enum Mode { MODE_A = 0; } }
                    """),
                ("companions_editions.proto", """
                    edition = "2023";
                    package companions.editions;
                    enum Ajar { AJAR_A = 0; }
                    enum Shut { option features.enum_type = CLOSED; SHUT_A = 1; }
                    """),
            ],
            "cpp_out");

        var checkedEnums = 0;
        foreach (var schema in files)
        {
            var header = File.ReadAllText(Path.Combine(directory, NameConventions.GetCppProtoHeader(schema))).ReplaceLineEndings("\n");
            var atNamespaceScope = string.Join(
                "\n", header.Split('\n').Where(line => Regex.IsMatch(line, @"^([^\s#/]|  \w+ =)")));

            foreach (var enumType in AllEnums(schema))
            {
                var type = NameConventions.GetCppTypeName(enumType);
                var limits = (enumType.ContainingType is null ? string.Empty : type + "_") + enumType.Name;
                var declared = Regex.Matches(atNamespaceScope, $@"\b(?:{Regex.Escape(type)}|{Regex.Escape(limits)})_\w*")
                    .Select(match => match.Value)
                    .Except(enumType.Values.Select(NameConventions.GetCppValueName).Append(type))
                    .Order(StringComparer.Ordinal);

                Assert.Equal(NameConventions.GetCppEnumCompanionNames(enumType).Order(StringComparer.Ordinal), declared);
                checkedEnums++;
            }
        }

        Assert.Equal(13, checkedEnums);
    }

    /// <summary>
    /// The namespace <see cref="NameConventions.GetCppNamespace"/> gives is the one the protoc header
    /// opens, one component at a time: a keyword and a macro escaped, and a generated member's name,
    /// which a class would have escaped, left alone.
    /// </summary>
    [Fact]
    public void TheCppNamespaceIsTheOneTheProtocHeaderOpens()
    {
        var (schema, header) = GenerateCppHeader(AwkwardTypeSchemaName, AwkwardTypeSchema);

        var opening = string.Concat(
            NameConventions.GetCppNamespace(schema).Split("::").Select(component => $"namespace {component} {{\n"));
        Assert.True(
            header.Contains(opening, StringComparison.Ordinal),
            $"protoc does not open the namespaces{Environment.NewLine}{opening}for the package '{schema.Package}'");
    }

    /// <summary>Every message the file declares, nested ones included.</summary>
    private static IEnumerable<MessageDescriptor> AllMessages(FileDescriptor schema)
        => schema.MessageTypes.SelectMany(WithNested);

    /// <summary>Every enum the file declares, top-level and nested.</summary>
    private static IEnumerable<EnumDescriptor> AllEnums(FileDescriptor schema)
        => schema.EnumTypes.Concat(AllMessages(schema).SelectMany(message => message.EnumTypes));

    private static IEnumerable<MessageDescriptor> WithNested(MessageDescriptor message)
        => message.NestedTypes.SelectMany(WithNested).Prepend(message);
}
