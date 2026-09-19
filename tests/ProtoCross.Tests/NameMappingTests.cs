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

/// <summary>
/// Covers the mapping from protobuf names and ProtoCross literals into each target language.
/// These are the cases where emitting something plausible-looking still fails to compile.
/// </summary>
public class NameMappingTests
{
    private static string Emit(IBackend backend, string source, string fileSuffix)
    {
        var path = TestPaths.WriteTempScript(source);
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.Module!, new BackendOptions(Path.GetFileName(path)), diagnostics);
        Assert.Empty(diagnostics);

        return files.Single(f => f.RelativePath.EndsWith(fileSuffix, StringComparison.Ordinal)).Contents;
    }

    private const string FixturePrelude = "import proto \"fixtures.proto\";\n";
    private const string BarePrelude = "import proto \"nopackage.proto\";\n";
    private const string CrossNamespacePrelude =
        "import proto \"cross_target.proto\";\nimport proto \"cross_caller.proto\";\n";

    [Fact]
    public void CSharpQualifiesTopLevelAndNestedEnums()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var top = status;
                    var inner = nested;
                    return count;
                }
            }
            """,
            "test.g.cs");

        Assert.Contains("global::ProtoCross.Tests.TopLevelStatus top", source, StringComparison.Ordinal);

        // protoc nests enums inside the containing message's Types class.
        Assert.Contains("global::ProtoCross.Tests.Outer.Types.Nested inner", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The explicit-return-type path, which only became reachable once the binder learned to
    /// resolve enum type references (issue #1).
    /// </summary>
    [Fact]
    public void CSharpQualifiesAnExplicitlyDeclaredEnumReturnType()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> TopLevelStatus {
                    var s: TopLevelStatus = status;
                    return s;
                }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "public static global::ProtoCross.Tests.TopLevelStatus F(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("global::ProtoCross.Tests.TopLevelStatus s = self.Status;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppQualifiesAnExplicitlyDeclaredEnumReturnType()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> protocross.tests.Outer.Nested {
                    var n: Nested = nested;
                    return n;
                }
            }
            """,
            "test.pc.h");

        Assert.Contains("inline ::protocross::tests::Outer_Nested f(", source, StringComparison.Ordinal);
        Assert.Contains("::protocross::tests::Outer_Nested n = self.nested();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensNestedEnumsWithUnderscores()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var top = status;
                    var inner = nested;
                    return count;
                }
            }
            """,
            "test.pc.h");

        Assert.Contains("::protocross::tests::TopLevelStatus top", source, StringComparison.Ordinal);
        Assert.Contains("::protocross::tests::Outer_Nested inner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpHandlesEnumsInAFileWithNoPackage()
    {
        var source = Emit(
            new CSharpBackend(),
            BarePrelude +
            """
            extend BareMessage {
                fn f() -> int64 {
                    var s = status;
                    return value;
                }
            }
            """,
            "test.g.cs");

        // Not "global::.BareStatus".
        Assert.Contains("global::BareStatus s", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandlesEnumsInAFileWithNoPackage()
    {
        var source = Emit(
            new CppBackend(),
            BarePrelude +
            """
            extend BareMessage {
                fn f() -> int64 {
                    var s = status;
                    return value;
                }
            }
            """,
            "test.pc.h");

        Assert.Contains("::BareStatus s", source, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpSuffixesFloatLiteralsWithF()
    {
        // 'return 1.5d;' would not compile: C# does not implicitly narrow double to float.
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn f() -> float { return 1.5; } }",
            "test.g.cs");

        Assert.Contains("return 1.5f;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpSuffixesDoubleLiteralsWithD()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn f() -> double { return 1.5; } }",
            "test.g.cs");

        Assert.Contains("return 1.5d;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppSuffixesFloatLiteralsWithF()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude + "extend Outer { fn f() -> float { return 1.5; } }",
            "test.pc.h");

        Assert.Contains("return 1.5f;", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void EscapesControlCharactersInStringLiterals(string target)
    {
        // The lexer decodes escapes, so the IR holds a real newline and tab here. Emitting them
        // raw would produce a literal spanning three lines, which neither language accepts.
        const string Script = """
            extend Outer {
                fn f() -> string { return "a\nb\tc\"d\\e"; }
            }
            """;

        var source = target == "csharp"
            ? Emit(new CSharpBackend(), FixturePrelude + Script, "test.g.cs")
            : Emit(new CppBackend(), FixturePrelude + Script, "test.pc.h");

        Assert.Contains("\"a\\nb\\tc\\\"d\\\\e\"", source, StringComparison.Ordinal);

        // The emitted literal must not have been split across lines.
        var literalLine = source.Split('\n').Single(line => line.Contains("return \"a", StringComparison.Ordinal));
        Assert.EndsWith(";", literalLine.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void CppEscapesMethodNamesThatAreKeywords()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn operator() -> int64 { return count; }
                fn caller() -> int64 { return operator(); }
            }
            """,
            "test.pc.h");

        // Declaration, definition, and call site must agree on the escaped spelling.
        Assert.Contains("inline ::std::int64_t operator_(", source, StringComparison.Ordinal);
        Assert.Contains("::protocross::tests::operator_(self)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("int64_t operator(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpPascalCaseAvoidsKeywordCollisions()
    {
        // Every C# reserved word is lowercase, so PascalCasing is already sufficient escaping.
        // 'class' is not a ProtoCross keyword, so it reaches the backend as an ordinary identifier.
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn class() -> int64 { return count; } }",
            "test.g.cs");

        Assert.Contains("public static long Class(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesCrossNamespaceMethodCalls()
    {
        var source = Emit(
            new CSharpBackend(),
            CrossNamespacePrelude +
            """
            extend protocross.target.Target {
                fn adjusted_value() -> int64 { return value + 1; }
            }

            extend protocross.caller.Caller {
                fn total() -> int64 {
                    if not has target {
                        return 0;
                    }

                    return target.adjusted_value();
                }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Target.TargetProtoCrossExtensions.AdjustedValue(self.Target);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Target.AdjustedValue()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpIncludesContainingMessagesInNestedReceiverExtensionClassNames()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend protocross.tests.Outer.Inner {
                fn f() -> int64 { return 1; }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "public static class Outer_InnerProtoCrossExtensions",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "this global::ProtoCross.Tests.Outer.Types.Inner self",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpMethodCallsDoNotBindToProtobufInstanceMembers()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn clone() -> int64 { return count; }
                fn caller() -> int64 { return clone(); }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.OuterProtoCrossExtensions.Clone(self);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("return self.Clone();", source, StringComparison.Ordinal);
    }

    // --- enum values ---
    //
    // protoc names an enum value completely differently in the two targets: C# strips the enum name
    // off the front and PascalCases the rest, while C++ keeps the .proto spelling and prefixes
    // nested enums with the flattened type name. Neither is derivable from the other, and a
    // near-miss emits an identifier that does not exist.

    private static string EmitEnumValue(
        IBackend backend,
        string prelude,
        string receiver,
        string returnType,
        string value,
        string suffix)
        => Emit(backend, prelude + $"extend {receiver} {{ fn f() -> {returnType} {{ return {value}; }} }}", suffix);

    [Fact]
    public void CSharpStripsTheEnumPrefixAndPascalCasesTheValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.g.cs");

        Assert.Contains("return global::ProtoCross.Tests.TopLevelStatus.Ok;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesANestedEnumValueThroughTheTypesClass()
    {
        var source = EmitEnumValue(
            new CSharpBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.Outer.Types.Nested.Some;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "protocross.tests.Outer.Inner.Deep",
            "protocross.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.Outer.Types.Inner.Types.Deep.None;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// protoc only strips a prefix the value actually carries, so a value named independently of
    /// its enum keeps every part of its name.
    /// </summary>
    [Fact]
    public void CSharpKeepsTheWholeNameOfAValueWithoutTheEnumPrefix()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.OTHER_RESULT",
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.TopLevelStatus.OtherResult;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Stripping the enum name from TOP_LEVEL_STATUS_2 leaves "2", which is not an identifier, so
    /// protoc prefixes an underscore. Emitting the bare digit would not compile.
    /// </summary>
    [Fact]
    public void CSharpUnderscoresAValueThatStripsToALeadingDigit()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_2",
            "test.g.cs");

        Assert.Contains("return global::ProtoCross.Tests.TopLevelStatus._2;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppLeavesATopLevelEnumValueUnprefixed()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.pc.h");

        Assert.Contains("return ::protocross::tests::TOP_LEVEL_STATUS_OK;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested enum has its values at namespace scope prefixed with the flattened enum name,
    /// rather than as members of the enum, so the qualification goes on the value and not the type.
    /// </summary>
    [Fact]
    public void CppPrefixesANestedEnumValueWithTheFlattenedEnumName()
    {
        var source = EmitEnumValue(
            new CppBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.pc.h");

        Assert.Contains(
            "return ::protocross::tests::Outer_Nested_NESTED_SOME;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "protocross.tests.Outer.Inner.Deep",
            "protocross.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.pc.h");

        Assert.Contains(
            "return ::protocross::tests::Outer_Inner_Deep_DEEP_NONE;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.g.cs");

        Assert.Contains("return global::BareStatus.Set;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.pc.h");

        Assert.Contains("return ::BARE_STATUS_SET;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", source, StringComparison.Ordinal);
    }

    // --- explicit conversions ---

    private static string EmitConversion(IBackend backend, string returnType, string expression, string suffix)
        => Emit(
            backend,
            FixturePrelude + $"extend Outer {{ fn f() -> {returnType} {{ return {expression}; }} }}",
            suffix);

    /// <summary>
    /// A narrowing conversion has to be unchecked. The conformance harness builds generated code
    /// with CheckForOverflowUnderflow, where a bare cast throws instead of wrapping.
    /// </summary>
    [Fact]
    public void CSharpNarrowsIntegersInsideUnchecked()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "count as int32", "test.g.cs");

        Assert.Contains("return unchecked((int)self.Count);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpConvertsToFloatingPointWithAPlainCast()
    {
        var source = EmitConversion(new CSharpBackend(), "double", "count as double", "test.g.cs");

        Assert.Contains("return (double)self.Count;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Floating point to integer is the one conversion C# leaves unspecified when out of range, and
    /// throws on under a checked build, so it cannot be a cast in either context.
    /// </summary>
    [Fact]
    public void CSharpRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "amount as int32", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Runtime.ProtoCrossArithmetic.ToInt32((double)self.Amount);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("(int)self.Amount", source, StringComparison.Ordinal);
    }

    /// <summary>A float source widens to double first, so one helper serves both widths.</summary>
    [Fact]
    public void CSharpWidensAFloatSourceBeforeConvertingToAnInteger()
    {
        var source = EmitConversion(new CSharpBackend(), "int64", "ratio as int64", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Runtime.ProtoCrossArithmetic.ToInt64((double)self.Ratio);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppNarrowsIntegersWithStaticCast()
    {
        var source = EmitConversion(new CppBackend(), "int32", "count as int32", "test.pc.h");

        Assert.Contains("return static_cast<::std::int32_t>(self.count());", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CppBackend(), "int32", "amount as int32", "test.pc.h");

        Assert.Contains(
            "return ::protocross_runtime::trunc_sat_f64_to_i32(static_cast<double>(self.amount()));",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Narrowing a double past the range of float is undefined behavior in C++, so that direction
    /// needs a helper even though widening does not.
    /// </summary>
    [Fact]
    public void CppRoutesDoubleToFloatThroughTheRuntimeButNotTheReverse()
    {
        var narrowing = EmitConversion(new CppBackend(), "float", "amount as float", "test.pc.h");
        var widening = EmitConversion(new CppBackend(), "double", "ratio as double", "test.pc.h");

        Assert.Contains(
            "return ::protocross_runtime::narrow_f64_to_f32(self.amount());",
            narrowing,
            StringComparison.Ordinal);
        Assert.Contains("return static_cast<double>(self.ratio());", widening, StringComparison.Ordinal);
    }

    /// <summary>A conversion to the type a value already has emits nothing at all.</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AnIdentityConversionEmitsTheOperandUnchanged(string target)
    {
        var isCSharp = target == "csharp";
        var source = EmitConversion(
            isCSharp ? new CSharpBackend() : new CppBackend(),
            "int64",
            "count as int64",
            isCSharp ? "test.g.cs" : "test.pc.h");

        Assert.Contains(
            isCSharp ? "return self.Count;" : "return self.count();",
            source,
            StringComparison.Ordinal);
    }

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
        var (messages, header) = GenerateWithPinnedProtoc(
            AwkwardFieldSchemaName,
            AwkwardFieldSchema,
            "cpp_out",
            "awkward_fields.pb.h");

        Assert.NotEmpty(messages);
        foreach (var message in messages)
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

    /// <summary>The protoc header for <see cref="AwkwardFieldSchema"/>, and the messages it declares.</summary>
    private static (IReadOnlyList<MessageDescriptor> Messages, string Header) GenerateCppHeader()
    {
        var (schema, header) = GenerateCppHeader(AwkwardFieldSchemaName, AwkwardFieldSchema);
        return ([.. schema.MessageTypes], header);
    }
    /// <summary>The protoc header for <paramref name="schemaText"/>, and the file it describes.</summary>
    /// <remarks>
    /// A property existing is not enough, because a wrong answer can be another field's right one:
    /// <c>not_like</c> misread as a group is <c>MyGroup</c>, which <c>mygroup</c> declares. protoc
    /// also names each field's number constant after its property, so that constant is what ties a
    /// name to the field it belongs to.
    /// </remarks>
    private static (FileDescriptor Schema, string Header) GenerateCppHeader(string schemaName, string schemaText)
    {
          /* Might be the proper body, might be the old body; arose during a merge conflict and resolution is unclear
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, schemaName), schemaText);

        var generated = Toolchain.RunProtoc(protoc, "cpp_out", directory, directory, schemaName);
        Assert.True(generated.ExitCode == 0, $"protoc could not generate C++.{Environment.NewLine}{generated.Output}");

        var schema = new DescriptorLoader(protoc)
            .LoadBundle([schemaName], [directory])
            .Descriptors.Single(file => file.Name == schemaName);

        var header = File.ReadAllText(Path.Combine(directory, NameConventions.GetCppProtoHeader(schema)));
        return (schema, header.ReplaceLineEndings("\n"));
      */
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
  
      private static string ClassBody(string header, string className)
    {
        var start = header.IndexOf($"class {className} final", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the generated header has no class {className}");

        var end = header.IndexOf("\n};", start, StringComparison.Ordinal);
        return header[start..end];
    }
  

    /// <summary>
    /// The declarations of one generated class, so a getter one message declares cannot answer for
    /// another: both messages here have a field called <c>descriptor</c>, spelled differently.
    /// </summary>

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

    // --- C# namespaces ---
    //
    // protoc's C# generator names a file's namespace with the case rule it names a property with, run
    // once over the whole package with its periods kept, unless the file sets csharp_namespace. A
    // package belongs to a file and the conformance corpus has one schema, so the rule is checked here
    // against the C# protoc writes, with one schema for each case it treats differently.

    /// <summary>
    /// What each schema declares ahead of its body: a package, and the option that overrides it.
    /// </summary>
    private static readonly string[] AwkwardPackageHeads =
    [
        "package acme.v1beta1;",
        "package acme.v1;",
        "package x9y.z;",
        "package snake_case.pkg_name;",
        "package a__b.c_;",
        "package mixedCase.ALLCAPS.lower;",
        "package _1x.acme;",
        "package __1x;",
        "package acme._1x;",
        "package acme.v1beta1;\noption csharp_namespace = \"Explicit.name_space\";",
        "package acme.v1beta1;\noption csharp_namespace = \"\";",
        "",
    ];

    /// <summary>
    /// Every namespace <see cref="NameConventions.GetCSharpNamespace"/> names is the one protoc
    /// declared for that file, for a schema of each case its rule treats differently: a letter after a
    /// digit, capitals, underscores doubled and trailing, an underscore before a digit at the start of
    /// the package and at the start of a later component, an explicit <c>csharp_namespace</c> and an
    /// empty one, and no package at all.
    /// </summary>
    /// <remarks>
    /// <c>acme._1x</c> is the case that tells protoc's rule from one applied a component at a time.
    /// protoc keeps the underscore in front of a digit only at the start of the whole package, so the
    /// namespace it declares is <c>Acme.1X</c>, which is not a C# identifier. The generated source
    /// does not compile, and the name ProtoCross gives it still has to be that one.
    /// </remarks>
    [Fact]
    public void EveryCSharpNamespaceIsTheOneProtocDeclaresForThatFile()
    {
        // protoc names a file's C# after the schema PascalCased, which Ns{index} already is.
        var schemas = AwkwardPackageHeads
            .Select((head, index) => ($"Ns{index}.proto", $"syntax = \"proto3\";\n{head}\n"))
            .ToList();
        var (files, directory) = GenerateWithPinnedProtoc(schemas, "csharp_out");

        foreach (var (file, head) in files.Zip(AwkwardPackageHeads))
        {
            var generated = File.ReadAllText(Path.Combine(directory, Path.ChangeExtension(file.Name, ".cs")));
            var declared = DeclaredNamespace(generated);
            var named = NameConventions.GetCSharpNamespace(file);
            Assert.True(
                named == declared,
                $"protoc declares the namespace '{declared}' for '{head}', and ProtoCross names it '{named}'");
        }
    }

    /// <summary>The namespace generated C# declares its classes in, or empty when it declares none.</summary>
    private static string DeclaredNamespace(string source)
    {
        var declaration = Regex.Match(source, @"^namespace (\S+) \{\r?$", RegexOptions.Multiline);
        return declaration.Success ? declaration.Groups[1].Value : string.Empty;
    }

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
