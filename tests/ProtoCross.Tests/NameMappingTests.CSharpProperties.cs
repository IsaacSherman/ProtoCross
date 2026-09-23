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
    // --- C# properties ---
    //
    // protoc's C# generator PascalCases a field name by a rule of its own, then escapes the result
    // when it is the message's name or a member every message declares. property_names.pcross runs
    // that rule end to end in both backends, for what a proto3 schema can hold. A group, and an
    // editions field named as though it were one, need an editions schema, so they are checked here
    // against the C# protoc writes, along with every member on the list.

    private const string AwkwardPropertySchemaName = "awkward_properties.proto";

    private const string AwkwardPropertySchema = """
        edition = "2023";

        package protocross.names;

        message AwkwardProperties {
          int64 awkward_properties = 1;
          int64 types = 2;
          int64 descriptor = 3;
          int64 equals = 4;
          int64 to_string = 5;
          int64 get_hash_code = 6;
          int64 write_to = 7;
          int64 clone = 8;
          int64 calculate_size = 9;
          int64 merge_from = 10;
          int64 on_construction = 11;
          int64 parser = 12;
          int64 get_type = 13;
          int64 field1a = 14;
          int64 a1b2c = 15;
          int64 _1st = 16;
          int64 __2nd = 17;
          int64 _private = 18;
          int64 trailing_ = 19;
          int64 mixedCASE = 20;
          int64 class = 21;
          int64 plain = 22;
        }

        message Clashing {
          int64 Descriptor = 1;
          int64 CLASHING = 2;
          int64 clashing = 3;
        }

        message GroupHolder {
          message MyGroup {
            int64 value = 1;
          }

          message RepeatedGroup {
            int64 value = 1;
          }

          MyGroup mygroup = 1 [features.message_encoding = DELIMITED];
          repeated RepeatedGroup repeatedgroup = 2 [features.message_encoding = DELIMITED];
          MyGroup not_like = 3 [features.message_encoding = DELIMITED];
          OtherThing otherthing = 4 [features.message_encoding = DELIMITED];
        }

        message PlainHolder {
          message MyGroup {
            int64 value = 1;
          }

          MyGroup mygroup = 1;
        }

        message OtherThing {
          int64 value = 1;
        }
        """;

    /// <summary>
    /// Every property <see cref="NameConventions.GetCSharpPropertyName"/> names is the one protoc
    /// declared for that field, and so is the presence test spelled from it, for a schema of the names
    /// its rule treats differently: the message's own name in and out of case, every member on
    /// protoc's list and one inherited member that is not, digits and underscores in each position,
    /// and delimited fields with and without the shape of a group.
    /// </summary>
    /// <remarks>
    /// A property existing is not enough, because a wrong answer can be another field's right one:
    /// <c>not_like</c> misread as a group is <c>MyGroup</c>, which <c>mygroup</c> declares. protoc
    /// also names each field's number constant after its property, so that constant is what ties a
    /// name to the field it belongs to.
    /// </remarks>
    [Fact]
    public void EveryCSharpPropertyNameIsTheOneTheProtocClassDeclaresForThatField()
    {
        var (messages, source) = GenerateWithPinnedProtoc(
            AwkwardPropertySchemaName,
            AwkwardPropertySchema,
            "csharp_out",
            "AwkwardProperties.cs");

        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            var declarations = CSharpClassBody(source, message.Name);
            foreach (var field in message.Fields.InDeclarationOrder())
            {
                var property = NameConventions.GetCSharpPropertyName(field);
                Assert.True(
                    DeclaresInstanceProperty(declarations, property),
                    $"protoc declares no property '{property}' on {message.Name} for the field '{field.Name}'");

                var numberConstant = $"public const int {property}FieldNumber = {field.FieldNumber};";
                Assert.True(
                    declarations.Contains(numberConstant, StringComparison.Ordinal),
                    $"protoc declares no '{numberConstant}' on {message.Name}, so '{property}' is not the "
                    + $"property of the field '{field.Name}'");

                if (field.HasPresence && field.FieldType is not (FieldType.Message or FieldType.Group))
                {
                    Assert.True(
                        DeclaresInstanceProperty(declarations, "Has" + property),
                        $"protoc declares no 'Has{property}' on {message.Name} for the field '{field.Name}'");
                }
            }
        }
    }

    /// <summary>
    /// The members one generated class declares ahead of its nested types, so a property one message
    /// declares cannot answer for another.
    /// </summary>
    private static string CSharpClassBody(string source, string className)
    {
        var start = source.IndexOf($"partial class {className} :", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the generated source has no class {className}");

        var end = source.IndexOf("partial class ", start + 1, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>Whether a generated class declares an instance property of this name.</summary>
    /// <remarks>
    /// Static members are excluded because they are exactly what a wrong answer would find: every
    /// message has a static <c>Descriptor</c> and <c>Parser</c>, and a field named for either is only
    /// correct if it is not reported as one of those.
    /// </remarks>
    private static bool DeclaresInstanceProperty(string declarations, string name)
        => Regex.IsMatch(
            declarations,
            $@"^\s*public (?!static )[^()=\r\n]+ {Regex.Escape(name)} \{{\r?$",
            RegexOptions.Multiline);
}
